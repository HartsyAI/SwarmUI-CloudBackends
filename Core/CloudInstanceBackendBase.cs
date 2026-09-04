using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Base for backends that rent a whole cloud instance running SwarmUI.
///
/// This backend is a control instance: it never generates anything itself. It starts the cloud
/// instance, then hands the resulting URL to SwarmUI's own <see cref="SwarmSwarmBackend"/> as a
/// non-real child backend. That child connects to the remote SwarmUI, enumerates its backends,
/// mirrors its models and features, and does all generation.
///
/// The deliberate consequence is that none of the remote-Swarm protocol lives here. Sessions,
/// model listing, websockets, previews, interrupts and parameter forwarding are all core Swarm code
/// that already works, rather than a reimplementation that has to be kept in step with it.
/// </summary>
public abstract class CloudInstanceBackendBase : AbstractT2IBackend, ICloudBackend
{
    /// <inheritdoc/>
    public string CloudProviderName => Provider?.ProviderName;

    /// <inheritdoc/>
    public string OwnerUserId { get; set; }

    /// <summary>The owning user, resolved fresh from the user DB, or null if the user no longer exists.</summary>
    public User Owner => string.IsNullOrWhiteSpace(OwnerUserId) ? null : Program.Sessions.GetUser(OwnerUserId, makeNew: false);

    /// <summary>The API key the current <see cref="Provider"/> was built with, for rotation detection.</summary>
    protected string ProviderApiKey;

    /// <inheritdoc/>
    public bool IsUsingApiKey(string apiKey) => apiKey == ProviderApiKey;

    /// <inheritdoc/>
    public JObject GetStatusNet()
    {
        return new JObject
        {
            ["id"] = BackendData?.ID,
            ["title"] = Title,
            ["provider"] = CloudProviderName ?? "Unknown",
            ["kind"] = "instance",
            ["status"] = Status.ToString(),
            ["instance_id"] = CurrentInstance?.InstanceId,
            ["instance_url"] = CurrentInstance?.PublicUrl,
            ["child_backend_id"] = ChildBackend?.ID
        };
    }

    /// <summary>Settings every instance-renting provider shares.</summary>
    public class InstanceSettings : AutoConfiguration
    {
        [ConfigComment("How long to wait for the instance to boot and for SwarmUI on it to answer, in seconds.\nA cold start that installs SwarmUI can take many minutes.")]
        public int StartupTimeoutSec = 900;

        [ConfigComment("How often to poll the provider while waiting for the instance to start, in milliseconds.")]
        public int PollIntervalMs = 5000;

        [ConfigComment("Failsafe: auto-stop the instance after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int MaxRuntimeMinutes = 0;

        [ConfigComment("Failsafe: auto-stop the instance once its estimated spend reaches this many US dollars.\nEstimated as (the provider's own reported hourly rate) x (time running) - not exact billing, and only checked for providers that report a rate (see GetCostPerHourAsync). Zero disables this check.")]
        public double MaxSpendUsd = 0;
    }

    /// <summary>Returns the subclass's settings cast to <see cref="InstanceSettings"/>.</summary>
    public abstract InstanceSettings InstanceConfig { get; }

    /// <summary>Factory: build a provider using the given API key.</summary>
    protected abstract ICloudInstanceProvider CreateProvider(string apiKey);

    /// <summary>Retrieve the provider API key for <paramref name="user"/>. Throw a readable error if missing.</summary>
    protected abstract string GetApiKey(User user);

    /// <summary>Throw <see cref="SwarmReadableErrorException"/> if the session user lacks permission.</summary>
    public abstract void CheckPermission(Session session);

    /// <summary>Throw if this backend's settings are unusable. Runs before the provider is built.</summary>
    protected virtual void CheckRequiredConfig() { }

    /// <summary>The live provider, once created in Init.</summary>
    public ICloudInstanceProvider Provider { get; private set; }

    /// <summary>The running instance, once started.</summary>
    public CloudInstanceInfo CurrentInstance { get; private set; }

    /// <summary>The child SwarmSwarmBackend attached to the instance, which does the actual work.</summary>
    public BackendHandler.BackendData ChildBackend { get; private set; }

    /// <summary>Guards start and stop so two requests cannot race the instance lifecycle.</summary>
    public SemaphoreSlim InstanceLock = new(1, 1);

    /// <summary>Control instances never load models or generate; the child backend does.</summary>
    public override IEnumerable<string> SupportedFeatures => [];

    // ── Runtime/spend failsafe ───────────────────────────────────────────────
    // Same Program.TickEvent subscribe-in-Init/unsubscribe-in-Shutdown idiom AutoScalingBackend uses for
    // its own periodic housekeeping; throttled internally since the tick fires roughly once a second.

    /// <summary>When the current instance was confirmed up. Null while no instance is running.</summary>
    public DateTime? InstanceStartedAt { get; private set; }

    /// <summary>Guards against the failsafe re-triggering while an async stop from a prior trip is still in flight.</summary>
    volatile bool FailsafeTripped = false;

    /// <summary><see cref="Environment.TickCount64"/> value before which <see cref="FailsafeTick"/> does nothing further.</summary>
    long NextFailsafeCheckTicks = 0;

    void FailsafeTick()
    {
        if (CurrentInstance is null || InstanceStartedAt is null || FailsafeTripped) { return; }
        if (InstanceConfig.MaxRuntimeMinutes <= 0 && InstanceConfig.MaxSpendUsd <= 0) { return; }
        long now = Environment.TickCount64;
        if (now < NextFailsafeCheckTicks) { return; }
        NextFailsafeCheckTicks = now + 30_000; // no need to check more than roughly twice a minute
        _ = Utilities.RunCheckedTask(CheckFailsafeAsync, $"{GetType().Name} #{BackendData?.ID} failsafe check");
    }

    async Task CheckFailsafeAsync()
    {
        if (CurrentInstance is null || InstanceStartedAt is null || FailsafeTripped) { return; }
        TimeSpan runtime = DateTime.UtcNow - InstanceStartedAt.Value;
        string reason = null;
        if (InstanceConfig.MaxRuntimeMinutes > 0 && runtime.TotalMinutes >= InstanceConfig.MaxRuntimeMinutes)
        {
            reason = $"max runtime of {InstanceConfig.MaxRuntimeMinutes}m reached (running {runtime.TotalMinutes:0.#}m)";
        }
        else if (InstanceConfig.MaxSpendUsd > 0)
        {
            try
            {
                double? costPerHour = await Provider.GetCostPerHourAsync(Program.GlobalProgramCancel);
                if (costPerHour is > 0)
                {
                    double estimatedSpend = costPerHour.Value * runtime.TotalHours;
                    if (estimatedSpend >= InstanceConfig.MaxSpendUsd)
                    {
                        reason = $"estimated spend ${estimatedSpend:0.00} reached cap of ${InstanceConfig.MaxSpendUsd:0.00} (at ${costPerHour:0.00}/hr)";
                    }
                }
            }
            // A failed rate lookup must not block the run - only the runtime cap is guaranteed regardless.
            catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] Failsafe spend check failed: {ex.Message}"); }
        }
        if (reason is null) { return; }
        FailsafeTripped = true;
        try
        {
            Logs.Warning($"[{Provider?.ProviderName}] Backend #{BackendData?.ID} auto-stopping instance: {reason}.");
            AddLoadStatus($"Auto-stopped: {reason}.");
            await StopInstanceAsync();
        }
        finally { FailsafeTripped = false; }
    }

    // ── Per-user instance persistence ─────────────────────────────────────────

    /// <summary>
    /// Per-user KV name this backend's created instance ID is remembered under (SaveGenericData
    /// lowercases it). Keyed by class plus the user-visible parent backend ID, so each provider section
    /// of each "Cloud Backends" entry remembers its own instance per user. Note this key follows the
    /// parent's ID: renumbering the backend via EditBackend's new_id orphans the remembered instance
    /// (same pre-existing property as the default pod naming scheme).
    /// </summary>
    string PersistName => $"{GetType().Name}_{BackendData?.AbstractParent?.ID ?? BackendData?.ID}";

    /// <summary>
    /// Instance ID remembered from a previous run for this owner, loaded at Init. Subclasses feed this
    /// to their provider as a reattach hint (used only after live verification, never trusted blindly),
    /// so a restart reattaches the owner's existing billed instance instead of creating a second one.
    /// </summary>
    protected string PersistedInstanceId { get; private set; }

    /// <summary>Remembers the provider's active instance ID in the owner's user data (no-op if unchanged or unknown).</summary>
    void PersistActiveInstanceId()
    {
        string id = Provider?.ActiveInstanceId;
        if (string.IsNullOrWhiteSpace(id) || id == PersistedInstanceId)
        {
            return;
        }
        PersistedInstanceId = id;
        Owner?.SaveGenericData("cloudbackends", PersistName, id);
        Logs.Debug($"[{Provider?.ProviderName}] Remembered instance '{id}' for user '{OwnerUserId}' under '{PersistName.ToLowerInvariant()}'.");
    }

    public override async Task Init()
    {
        Program.TickEvent += FailsafeTick;
        AddLoadStatus($"Starting {GetType().Name} backend...");
        string apiKey;
        try
        {
            CheckRequiredConfig();
            // Every cloud backend runs on its owner's own key - there is deliberately no fallback to
            // any other user's key, so a misconfigured owner is a hard error rather than a mis-bill.
            User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend has no valid owner user ('{OwnerUserId}').");
            apiKey = GetApiKey(owner);
            PersistedInstanceId = owner.GetGenericData("cloudbackends", PersistName)?.Trim();
        }
        catch (Exception ex)
        {
            AddLoadStatus($"ERROR: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        ProviderApiKey = apiKey;
        Provider = CreateProvider(apiKey);
        try
        {
            AddLoadStatus($"Validating {Provider.ProviderName} credentials and configuration...");
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            await Provider.ValidateAsync(cancel.Token);
        }
        catch (Exception ex)
        {
            AddLoadStatus($"ERROR: {Provider.ProviderName} validation failed: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        // A control instance must not be offered for generation itself.
        CanLoadModels = false;
        MaxUsages = 1;
        // The instance is deliberately NOT started here: renting one bills continuously, and a backend
        // that comes into being on its owner's first use must never do that on its own. Start is always
        // an explicit action (the Start button, or the start API route).
        Status = BackendStatus.RUNNING;
        AddLoadStatus($"{Provider.ProviderName} backend ready. The instance is not started yet; press Start to rent one.");
    }

    /// <summary>
    /// Re-resolves the owner's API key and rebuilds the provider if it changed, so a key rotation takes
    /// effect on the next start instead of requiring a disable/re-enable cycle. Must be called under
    /// <see cref="InstanceLock"/>. Safe while no instance is attached; the persisted instance ID (not
    /// in-memory provider state) is what carries reattachment across the rebuild.
    /// </summary>
    void RefreshProviderIfKeyChanged()
    {
        User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend's owner user ('{OwnerUserId}') no longer exists.");
        string currentKey = GetApiKey(owner);
        if (currentKey == ProviderApiKey)
        {
            return;
        }
        Logs.Info($"[{Provider?.ProviderName}] API key changed for user '{OwnerUserId}', rebuilding provider for backend #{BackendData?.ID}.");
        ICloudInstanceProvider oldProvider = Provider;
        Provider = CreateProvider(currentKey);
        ProviderApiKey = currentKey;
        oldProvider?.Dispose();
    }

    /// <summary>
    /// Starts the cloud instance if it is not already running, and attaches the owner-bound swarm
    /// child to it. Safe to call repeatedly.
    /// </summary>
    public async Task StartInstanceAsync()
    {
        await InstanceLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            if (ChildBackend is not null && CurrentInstance is not null)
            {
                return;
            }
            RefreshProviderIfKeyChanged();
            AddLoadStatus($"Starting {Provider.ProviderName} instance (up to {InstanceConfig.StartupTimeoutSec}s)...");
            try
            {
                CurrentInstance = await Provider.StartInstanceAsync(InstanceConfig.StartupTimeoutSec, InstanceConfig.PollIntervalMs, Program.GlobalProgramCancel);
            }
            finally
            {
                // Remember the instance even when the start times out partway: it may already have been
                // created (and be billing), and the remembered ID is what lets the next attempt reattach
                // it rather than create a second one.
                PersistActiveInstanceId();
            }
            AddLoadStatus($"Instance '{CurrentInstance.InstanceId}' is up at {CurrentInstance.PublicUrl}, attaching Swarm backend...");
            AttachChildBackend();
            InstanceStartedAt = DateTime.UtcNow;
            AddLoadStatus($"{Provider.ProviderName} instance ready.");
        }
        finally { InstanceLock.Release(); }
    }

    /// <summary>
    /// Hands the instance URL to a swarm backend as a non-real child. That child is a control instance
    /// in its own right: it mirrors the remote's backends, models and features, and spawns its own
    /// children which perform generation. The child is an <see cref="OwnerBoundSwarmBackend"/> (not
    /// core's plain SwarmSwarmBackend) so the whole tree refuses generations from anyone but the owner.
    /// </summary>
    internal void AttachChildBackend()
    {
        SwarmSwarmBackend.SwarmSwarmBackendSettings settings = new()
        {
            Address = CurrentInstance.PublicUrl,
            // AllowIdle does two things we need: SwarmSwarmBackend only re-polls the remote's backend
            // list (ReviseRemoteDataList) via its idle monitor, so without this a backend added on the
            // instance after attach (or removed and re-added) is never picked up without a manual
            // restart. It also lets a transient connectivity blip recover on its own by going IDLE
            // instead of ERRORED, which matters more here than for a same-machine remote since a cloud
            // proxy URL is more prone to brief hiccups.
            AllowIdle = true,
            AllowForwarding = false,
            AllowWebsocket = true,
            ConnectionAttemptTimeoutSeconds = Math.Max(30, InstanceConfig.StartupTimeoutSec / 4)
        };
        ChildBackend = Handler.AddNewNonrealBackend(CloudBackendTypes.OwnerBoundSwarm, BackendData, settings, newData =>
        {
            SwarmSwarmBackend swarm = newData.AbstractBackend as SwarmSwarmBackend;
            swarm.IsSpecialControlled = true;
            swarm.CanLoadModels = false;
            swarm.Title = $"[{Provider.ProviderName} {CurrentInstance.InstanceId}] {(string.IsNullOrWhiteSpace(CurrentInstance.Description) ? "Cloud Instance" : CurrentInstance.Description)}";
            // Core's AddNewNonrealBackend takes a parent argument but never assigns it - set it
            // ourselves, since OwnerBoundSwarmBackend's ownership walk relies on this exact link.
            newData.AbstractParent = BackendData;
            newData.UpdateLastReleaseTime();
        });
        Logs.Info($"[{Provider.ProviderName}] Attached Swarm backend #{ChildBackend.ID} to instance '{CurrentInstance.InstanceId}'.");
    }

    /// <summary>Removes the child backend and releases the cloud instance.</summary>
    public async Task StopInstanceAsync()
    {
        await InstanceLock.WaitAsync(CancellationToken.None);
        try
        {
            if (ChildBackend is not null)
            {
                int id = ChildBackend.ID;
                ChildBackend = null;
                try { await Handler.DeleteById(id); }
                catch (Exception ex) { Logs.Debug($"[{Provider?.ProviderName}] Removing child backend #{id} failed: {ex.Message}"); }
            }
            if (Provider is not null && CurrentInstance is not null)
            {
                // Releasing matters more than tidiness: a running instance bills until it is stopped.
                try { await Provider.ReleaseInstanceAsync(); }
                catch (Exception ex) { Logs.Error($"[{Provider.ProviderName}] Failed to release instance '{CurrentInstance.InstanceId}', it may still be billing: {ex.ReadableString()}"); }
            }
            CurrentInstance = null;
            InstanceStartedAt = null;
        }
        finally { InstanceLock.Release(); }
    }

    public override async Task Shutdown()
    {
        Program.TickEvent -= FailsafeTick;
        string name = Provider?.ProviderName ?? GetType().Name;
        Logs.Info($"[{name}] Backend {BackendData?.ID} shutting down...");
        await StopInstanceAsync();
        Provider?.Dispose();
        Provider = null;
        Status = BackendStatus.DISABLED;
    }

    /// <inheritdoc/>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        input.RefusalReasons.Add($"{Provider?.ProviderName ?? "Cloud instance"} control backends do not generate directly; their attached cloud Swarm backend does.");
        return false;
    }

    public override Task<Image[]> Generate(T2IParamInput user_input)
    {
        throw new NotImplementedException("Cloud instance control backends do not generate; the attached Swarm backend does.");
    }

    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        throw new NotImplementedException("Cloud instance control backends do not load models; the attached Swarm backend does.");
    }
}
