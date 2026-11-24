using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Base for backends that rent a whole cloud instance running SwarmUI. It is a control instance that never generates: it starts the instance, then hands the URL to core's <see cref="SwarmSwarmBackend"/> as a non-real child,
/// which connects to the remote SwarmUI, mirrors its backends, models and features, and does all generation.
/// None of the remote-Swarm protocol (sessions, model listing, websockets, previews, interrupts, parameter forwarding) lives here, so it cannot drift from core.</summary>
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

    /// <summary>Settings every instance-renting provider shares, copied in from the card; the card's <see cref="CloudBackendsBackend.Settings"/> documents them.</summary>
    public class InstanceSettings : AutoConfiguration
    {
        /// <summary>Seconds to wait for the instance to boot and for SwarmUI on it to answer.</summary>
        public int StartupTimeoutSec = 900;

        /// <summary>Failsafe: stop after this many minutes running. Zero disables it.</summary>
        public int MaxRuntimeMinutes = 0;

        /// <summary>Failsafe: stop once the estimated spend (the provider's hourly rate times time running) reaches this many US dollars. Zero disables it.</summary>
        public double MaxSpendUsd = 0;

        /// <summary>Leave the instance running when SwarmUI itself shuts down, and reattach it on the next run. Off releases it, as any other shutdown does.</summary>
        public bool KeepRunningOnShutdown = false;
    }

    /// <summary>How often to poll the provider while an instance starts, in milliseconds.</summary>
    const int PollIntervalMs = 5000;

    /// <summary>Returns the subclass's settings cast to <see cref="InstanceSettings"/>.</summary>
    public abstract InstanceSettings InstanceConfig { get; }

    /// <summary>Creates a provider on the given API key.</summary>
    protected abstract ICloudInstanceProvider CreateProvider(string apiKey);

    /// <summary>The provider API key of <paramref name="user"/>. Throws a readable error if missing.</summary>
    protected abstract string GetApiKey(User user);

    /// <summary>Throw <see cref="SwarmReadableErrorException"/> if the session user lacks permission.</summary>
    public abstract void CheckPermission(Session session);

    /// <summary>Throws if this backend's settings are unusable. Runs before the provider is built.</summary>
    protected virtual void CheckRequiredConfig()
    {
    }

    /// <summary>The live provider, once created in Init.</summary>
    public ICloudInstanceProvider Provider;

    /// <summary>The running instance, once started.</summary>
    public CloudInstanceInfo CurrentInstance;

    /// <summary>The child SwarmSwarmBackend attached to the instance, which does the actual work.</summary>
    public BackendHandler.BackendData ChildBackend;

    /// <summary>Guards start and stop so two requests cannot race the instance lifecycle.</summary>
    public readonly SemaphoreSlim InstanceLock = new(1, 1);

    /// <summary>Cancelled when this backend shuts down, so a start in progress gives up instead of holding the lock for minutes.</summary>
    CancellationTokenSource Lifetime = new();

    /// <inheritdoc/>
    /// <remarks>None: a control instance never loads models or generates, its child backend does.</remarks>
    public override IEnumerable<string> SupportedFeatures => [];

    /// <summary>When the current instance was confirmed up. Null while no instance is running.</summary>
    public DateTime? InstanceStartedAt;

    /// <summary>Guards against the failsafe re-triggering while an async stop from a prior trip is still in flight.</summary>
    volatile bool FailsafeTripped = false;

    /// <summary><see cref="Environment.TickCount64"/> value before which <see cref="FailsafeTick"/> does nothing further.</summary>
    long NextFailsafeCheckTicks = 0;

    /// <summary>Starts a failsafe check, at most about twice a minute, while a runtime or spend cap is set.</summary>
    void FailsafeTick()
    {
        if (!HasBillableInstance || InstanceStartedAt is null || FailsafeTripped)
        {
            return;
        }
        if (InstanceConfig.MaxRuntimeMinutes <= 0 && InstanceConfig.MaxSpendUsd <= 0)
        {
            return;
        }
        long now = Environment.TickCount64;
        if (now < NextFailsafeCheckTicks)
        {
            return;
        }
        NextFailsafeCheckTicks = now + 30_000;
        _ = Utilities.RunCheckedTask(CheckFailsafeAsync, $"{GetType().Name} #{BackendData?.ID} failsafe check");
    }

    /// <summary>True while this backend holds an instance that may be billing: attached, or created or started by a start that has not
    /// finished (or failed). An instance only named in the settings, and never started here, is not ours to stop.</summary>
    bool HasBillableInstance => CurrentInstance is not null || (InstanceStartedAt is not null && !string.IsNullOrWhiteSpace(Provider?.ActiveInstanceId));

    /// <summary>True while this backend holds an instance it would release on Stop, attached or not (see <see cref="HasBillableInstance"/>).</summary>
    public bool HoldsInstance => HasBillableInstance;

    /// <summary>Stops the instance when the runtime cap or the estimated spend cap is reached. A failed rate lookup does not block the run; only the runtime cap is guaranteed.</summary>
    async Task CheckFailsafeAsync()
    {
        if (!HasBillableInstance || InstanceStartedAt is null || FailsafeTripped)
        {
            return;
        }
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
            catch (Exception ex)
            {
                Logs.Verbose($"[{Provider?.ProviderName}] Failsafe spend check failed: {ex.Message}");
            }
        }
        if (reason is null)
        {
            return;
        }
        FailsafeTripped = true;
        try
        {
            Logs.Warning($"[{Provider?.ProviderName}] Backend #{BackendData?.ID} auto-stopping instance: {reason}.");
            AddLoadStatus($"Auto-stopped: {reason}.");
            await StopInstanceAsync();
        }
        finally
        {
            FailsafeTripped = false;
        }
    }

    /// <summary>Per-user key the active instance ID is remembered under: class plus the parent backend's ID.</summary>
    string PersistName => PersistKey(GetType().Name, BackendData?.AbstractParent?.ID ?? BackendData?.ID ?? 0);

    /// <summary>The per-user key a backend type under a card remembers its instance under.</summary>
    public static string PersistKey(string backendTypeName, int cardId) => $"{backendTypeName}_{cardId}";

    /// <summary>Per-user key of the start record, "{id} {start time}", written when this backend starts or reattaches an instance and removed when
    /// it releases it. It marks the instance as held by this backend, and keeps the safety stops counting from the first start across a restart.</summary>
    string StartRecordName => $"{PersistName}_started";

    /// <summary>A start record for <paramref name="instanceId"/>.</summary>
    internal static string FormatStartRecord(string instanceId, DateTime startedUtc) => $"{instanceId} {startedUtc.ToUniversalTime():O}";

    /// <summary>The start time in a start record when the record is for <paramref name="instanceId"/>, else null.</summary>
    internal static DateTime? ParseStartRecord(string record, string instanceId)
    {
        string[] parts = record?.Trim().Split(' ', 2) ?? [];
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(instanceId) || parts[0] != instanceId.Trim())
        {
            return null;
        }
        return DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started) ? started.ToUniversalTime() : null;
    }

    /// <summary>When a reattached instance started billing: its start record, else now minus the uptime the provider reports, else now.</summary>
    internal static DateTime RestoredStartTime(DateTime? recorded, int uptimeSeconds, DateTime nowUtc)
    {
        if (recorded is DateTime started && started <= nowUtc)
        {
            return started;
        }
        return uptimeSeconds > 0 ? nowUtc.AddSeconds(-uptimeSeconds) : nowUtc;
    }

    /// <summary>True if a shutdown releases the instance: always, unless Keep Running On Shutdown is on and SwarmUI itself is exiting. Saving, disabling or deleting the card still releases it.</summary>
    internal static bool ReleasesOnShutdown(bool keepRunning, bool swarmExiting) => !(keepRunning && swarmExiting);

    /// <summary>True if the user has an instance that a backend of this type under this card started and has not released.</summary>
    public static bool HasHeldInstance(User user, string backendTypeName, int cardId)
    {
        string key = PersistKey(backendTypeName, cardId);
        return ParseStartRecord(user?.GetGenericData("cloudbackends", $"{key}_started"), user?.GetGenericData("cloudbackends", key)) is not null;
    }

    /// <summary>Instance ID remembered from a previous run for this owner, loaded at Init. Subclasses feed it to their provider as a reattach hint, used only after live verification, so a restart reattaches the owner's billed instance instead of creating a second one.</summary>
    protected string PersistedInstanceId;

    /// <summary>The gateway token for this owner's instance, created once and remembered with the instance ID so a restart can reattach. Injected into instances this backend creates and sent by the attached child. Never logged.</summary>
    protected string WorkerToken;

    /// <summary>A token set in the card for an existing machine you created yourself, or null to use (and inject) this backend's own.</summary>
    protected virtual string ExistingWorkerToken => null;

    /// <summary>How many instance IDs per provider are remembered for orphan detection.</summary>
    const int KnownInstanceLimit = 50;

    /// <summary>Makes each read-modify-write of a remembered-instances list atomic, so two cards finishing at once cannot drop each other's ID.</summary>
    static readonly object KnownInstancesLock = new();

    /// <summary>Adds an instance ID to the owner's list of instances this extension created or attached for a provider. Kept apart from the active ID, which a newer instance replaces, so orphan detection recognises machines with any name or label.</summary>
    public static void RememberKnownInstance(User owner, string backendTypeName, string id)
    {
        if (owner is null || string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        lock (KnownInstancesLock)
        {
            List<string> known = KnownInstances(owner, backendTypeName);
            known.Remove(id);
            known.Add(id);
            if (known.Count > KnownInstanceLimit)
            {
                known.RemoveRange(0, known.Count - KnownInstanceLimit);
            }
            owner.SaveGenericData("cloudbackends", $"known_{backendTypeName}", new JArray(known).ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    /// <summary>Instance IDs this extension has created or attached for the owner, for one provider's backend type.</summary>
    public static List<string> KnownInstances(User owner, string backendTypeName)
    {
        string raw = owner?.GetGenericData("cloudbackends", $"known_{backendTypeName}");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        try
        {
            return [.. JArray.Parse(raw).Select(t => t.ToString())];
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Remembers the provider's active instance ID in the owner's user data, doing nothing if it is unknown. The known-instances list is updated even when the ID is unchanged, so an ID missing from the orphan list (older install, or evicted) rejoins it.</summary>
    void PersistActiveInstanceId()
    {
        string id = Provider?.ActiveInstanceId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        RememberKnownInstance(Owner, GetType().Name, id);
        if (InstanceStartedAt is DateTime started && ParseStartRecord(Owner?.GetGenericData("cloudbackends", StartRecordName), id) is null)
        {
            Owner?.SaveGenericData("cloudbackends", StartRecordName, FormatStartRecord(id, started));
        }
        if (id == PersistedInstanceId)
        {
            return;
        }
        PersistedInstanceId = id;
        Owner?.SaveGenericData("cloudbackends", PersistName, id);
        Logs.Debug($"[{Provider?.ProviderName}] Remembered instance '{id}' for user '{OwnerUserId}' under '{PersistName.ToLowerFast()}'.");
    }

    /// <inheritdoc/>
    /// <remarks>Every cloud backend runs on its owner's own key, with no fallback to another user's, so a misconfigured owner is a hard error rather than a mis-bill.
    /// The instance is never started here: renting bills, so starting is always an explicit action (Start, or the start route). FailsafeTick is unsubscribed first, since a re-enable can Init without a Shutdown.</remarks>
    public override async Task Init()
    {
        Program.TickEvent -= FailsafeTick;
        Program.TickEvent += FailsafeTick;
        Lifetime.Dispose();
        Lifetime = new CancellationTokenSource();
        AddLoadStatus($"Starting {GetType().Name} backend...");
        string apiKey;
        try
        {
            CheckRequiredConfig();
            User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend has no valid owner user ('{OwnerUserId}').");
            apiKey = GetApiKey(owner);
            PersistedInstanceId = owner.GetGenericData("cloudbackends", PersistName)?.Trim();
            RememberKnownInstance(owner, GetType().Name, PersistedInstanceId);
            WorkerToken = string.IsNullOrWhiteSpace(ExistingWorkerToken) ? owner.GetGenericData("cloudbackends", $"{PersistName}_token")?.Trim() : ExistingWorkerToken.Trim();
            if (string.IsNullOrWhiteSpace(WorkerToken))
            {
                WorkerToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(36)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                owner.SaveGenericData("cloudbackends", $"{PersistName}_token", WorkerToken);
            }
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
        CanLoadModels = false;
        MaxUsages = 1;
        await ReattachKeptInstanceAsync();
        Status = BackendStatus.RUNNING;
        AddLoadStatus(HasBillableInstance ? $"{Provider.ProviderName} backend ready, holding instance '{Provider.ActiveInstanceId}'." : $"{Provider.ProviderName} backend ready. The instance is not started yet; press Start to rent one.");
        if (HasBillableInstance)
        {
            ScheduleReconnect();
        }
    }

    /// <summary>True once the instance an earlier run left running has been looked up, or there was none to look up. A failed lookup leaves it false, so the next status or Stop retries.</summary>
    volatile bool ReattachChecked = false;

    /// <summary>With Keep Running On Shutdown, binds this backend to the instance an earlier SwarmUI run left running, so status, Stop and the safety stops cover it again without anyone pressing Start.
    /// Never starts anything, and never throws: a failed lookup is retried on the next call. Init reconnects once it is RUNNING; a later reattach, such as a status call retrying a failed lookup, reconnects here.</summary>
    public async Task ReattachKeptInstanceAsync()
    {
        if (ReattachChecked || !InstanceConfig.KeepRunningOnShutdown)
        {
            return;
        }
        try
        {
            await InstanceLock.WaitAsync(Program.GlobalProgramCancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        bool reattached;
        try
        {
            reattached = await ReattachKeptInstanceLockedAsync();
        }
        finally
        {
            InstanceLock.Release();
        }
        if (reattached && Status == BackendStatus.RUNNING)
        {
            ScheduleReconnect();
        }
    }

    /// <summary><see cref="ReattachKeptInstanceAsync"/>, for a caller already holding <see cref="InstanceLock"/>. True if it reattached an instance. Only an instance this backend started and never released is reattached; a provider bound to another instance (a PodId set since) is left alone.</summary>
    async Task<bool> ReattachKeptInstanceLockedAsync()
    {
        if (ReattachChecked || !InstanceConfig.KeepRunningOnShutdown || Provider is null)
        {
            return false;
        }
        string id = PersistedInstanceId;
        DateTime? recorded = ParseStartRecord(Owner?.GetGenericData("cloudbackends", StartRecordName), id);
        if (recorded is null || HasBillableInstance || (!string.IsNullOrWhiteSpace(Provider.ActiveInstanceId) && Provider.ActiveInstanceId != id))
        {
            ReattachChecked = true;
            return false;
        }
        try
        {
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            CloudInstanceStatus status = await Provider.AdoptInstanceAsync(id, cancel.Token);
            ReattachChecked = true;
            if (status is null)
            {
                Logs.Info($"[{Provider.ProviderName}] Instance '{id}' kept from an earlier run is gone.");
                Owner?.DeleteGenericData("cloudbackends", StartRecordName);
                return false;
            }
            InstanceStartedAt = RestoredStartTime(recorded, status.UptimeSeconds, DateTime.UtcNow);
            AddLoadStatus($"Reattached instance '{id}' ({status.Status}), kept from an earlier SwarmUI run. It is held until Stop releases it.");
            Logs.Info($"[{Provider.ProviderName}] Backend #{BackendData?.ID} reattached instance '{id}' ({status.Status}, first started {InstanceStartedAt:u}); the safety stops apply again.");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[{Provider.ProviderName}] Could not check instance '{id}' kept from an earlier run, will retry: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reconnects, in the background, the Swarm backend of an instance reattached after a restart.</summary>
    void ScheduleReconnect()
    {
        _ = Utilities.RunCheckedTask(ReconnectKeptInstanceAsync, $"{GetType().Name} #{BackendData?.ID} reconnect");
    }

    /// <summary>Attaches the Swarm backend of a reattached instance once its SwarmUI answers, so it generates again without pressing Start. Never starts, resumes or creates anything, and holds <see cref="InstanceLock"/> only to attach,
    /// so Stop is never kept waiting on the connection. Does nothing if it was stopped, released or attached by a Start while waiting.</summary>
    async Task ReconnectKeptInstanceAsync()
    {
        ICloudInstanceProvider provider = Provider;
        string id = provider?.ActiveInstanceId;
        if (provider is null || string.IsNullOrWhiteSpace(id) || ChildBackend is not null)
        {
            return;
        }
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, Program.GlobalProgramCancel);
        try
        {
            CloudInstanceInfo info = await provider.ConnectInstanceAsync(InstanceConfig.StartupTimeoutSec, PollIntervalMs, cancel.Token);
            if (info is null)
            {
                return;
            }
            await InstanceLock.WaitAsync(cancel.Token);
            try
            {
                if (Status != BackendStatus.RUNNING || ChildBackend is not null || CurrentInstance is not null || Provider != provider || !HasBillableInstance)
                {
                    return;
                }
                CurrentInstance = info;
                AttachChildBackend();
                AddLoadStatus($"Reconnected to instance '{id}' at {info.PublicUrl}.");
            }
            finally
            {
                InstanceLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Provider == provider && HasBillableInstance && ChildBackend is null)
            {
                AddLoadStatus($"Instance '{id}' is held, but its SwarmUI did not answer ({ex.Message}). Press Start to reconnect it, or Stop to release it.");
            }
        }
    }

    /// <summary>Rebuilds the provider if the owner's API key changed, so a new key applies on the next start. Must be called under <see cref="InstanceLock"/>.</summary>
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

    /// <summary>True while a start is in progress: the provider may already know the instance's ID, but the child is not attached yet.</summary>
    public volatile bool Starting = false;

    /// <summary>Starts the cloud instance if it is not already running, and attaches the owner-bound swarm child to it. Safe to call repeatedly.
    /// The runtime and spend caps count from the start, so they also cover an instance whose SwarmUI never answers, and a held instance keeps its first start.
    /// The instance ID is remembered even when the start fails partway, since it may already exist and bill, so the next start can reattach it.</summary>
    public async Task StartInstanceAsync()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, Program.GlobalProgramCancel);
        await InstanceLock.WaitAsync(cancel.Token);
        Starting = true;
        try
        {
            if (Status != BackendStatus.RUNNING)
            {
                throw new SwarmReadableErrorException("This cloud backend is not running (it was disabled, or failed to start), so it cannot start an instance.");
            }
            if (ChildBackend is not null && CurrentInstance is not null)
            {
                return;
            }
            RefreshProviderIfKeyChanged();
            AddLoadStatus($"Starting {Provider.ProviderName} instance (up to {InstanceConfig.StartupTimeoutSec}s)...");
            InstanceStartedAt ??= DateTime.UtcNow;
            try
            {
                CurrentInstance = await Provider.StartInstanceAsync(InstanceConfig.StartupTimeoutSec, PollIntervalMs, cancel.Token);
            }
            catch (Exception)
            {
                if (string.IsNullOrWhiteSpace(Provider?.ActiveInstanceId))
                {
                    InstanceStartedAt = null;
                }
                else
                {
                    AddLoadStatus($"Instance '{Provider.ActiveInstanceId}' was not ready in time and is still running (and billing). Start again to reattach it, or Stop to release it.");
                }
                throw;
            }
            finally
            {
                PersistActiveInstanceId();
            }
            AddLoadStatus($"Instance '{CurrentInstance.InstanceId}' is up at {CurrentInstance.PublicUrl}, attaching Swarm backend...");
            AttachChildBackend();
            AddLoadStatus($"{Provider.ProviderName} instance ready.");
        }
        finally
        {
            Starting = false;
            InstanceLock.Release();
        }
    }

    /// <summary>Hands the instance URL to an owner-bound swarm child, which does all the real work.</summary>
    internal void AttachChildBackend()
    {
        string title = $"[{Provider.ProviderName} {CurrentInstance.InstanceId}] {(string.IsNullOrWhiteSpace(CurrentInstance.Description) ? "Cloud Instance" : CurrentInstance.Description)}";
        ChildBackend = CloudChildBackend.Attach(this, GetChildAddress(CurrentInstance.PublicUrl), title, InstanceConfig.StartupTimeoutSec, $"Bearer {WorkerToken}");
        Logs.Info($"[{Provider.ProviderName}] Attached Swarm backend #{ChildBackend.ID} to instance '{CurrentInstance.InstanceId}'.");
    }

    /// <inheritdoc/>
    /// <remarks>Nothing to do: a rented instance bills for wall-clock time until stopped, so generation activity changes nothing about its lifetime.</remarks>
    public Task OnChildGenerationStartingAsync(SwarmSwarmBackend control) => Task.CompletedTask;

    /// <summary>The address the child connects to for an instance URL. Vast.ai overrides this with a TLS relay.</summary>
    protected virtual string GetChildAddress(string publicUrl)
    {
        return publicUrl;
    }

    /// <summary>Called after the child is detached, to free anything <see cref="GetChildAddress"/> created.</summary>
    protected virtual Task OnChildDetachedAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>Removes the child backend and releases the cloud instance. Returns false if there was no instance to release. Also releases an instance kept from an earlier run that Init could not look up,
    /// and one a failed start created or reattached, which has no CurrentInstance but bills all the same. Releasing matters more than tidiness, so a failed release is reported as possibly still billing.</summary>
    public async Task<bool> StopInstanceAsync()
    {
        await InstanceLock.WaitAsync(CancellationToken.None);
        bool released = false;
        try
        {
            await ReattachKeptInstanceLockedAsync();
            if (ChildBackend is not null)
            {
                BackendHandler.BackendData child = ChildBackend;
                ChildBackend = null;
                await CloudChildBackend.DetachAsync(Handler, child, Provider?.ProviderName);
                await OnChildDetachedAsync();
            }
            if (Provider is not null && HasBillableInstance)
            {
                string instanceId = CurrentInstance?.InstanceId ?? Provider.ActiveInstanceId;
                try
                {
                    await Provider.ReleaseInstanceAsync();
                    released = true;
                    Owner?.DeleteGenericData("cloudbackends", StartRecordName);
                }
                catch (Exception ex)
                {
                    Logs.Error($"[{Provider.ProviderName}] Failed to release instance '{instanceId}', it may still be billing: {ex.ReadableString()}");
                    throw new SwarmReadableErrorException($"Could not release instance '{instanceId}'; it may still be billing: {ex.Message}");
                }
            }
            CurrentInstance = null;
            InstanceStartedAt = null;
        }
        finally
        {
            InstanceLock.Release();
        }
        return released;
    }

    /// <inheritdoc/>
    /// <remarks>A start still in progress gives up and releases the lock, then the stop releases anything it created. With Keep Running On Shutdown and SwarmUI itself exiting, the instance is left running instead.
    /// A failed release is already logged, so shutdown carries on regardless.</remarks>
    public override async Task Shutdown()
    {
        Program.TickEvent -= FailsafeTick;
        Status = BackendStatus.DISABLED;
        Lifetime.Cancel();
        string name = Provider?.ProviderName ?? GetType().Name;
        Logs.Info($"[{name}] Backend {BackendData?.ID} shutting down...");
        try
        {
            if (ReleasesOnShutdown(InstanceConfig.KeepRunningOnShutdown, Program.GlobalProgramCancel.IsCancellationRequested))
            {
                await StopInstanceAsync();
            }
            else
            {
                await LeaveInstanceRunningAsync();
            }
        }
        catch (SwarmReadableErrorException)
        {
        }
        Provider?.Dispose();
        Provider = null;
    }

    /// <summary>Detaches the child but leaves the instance running, with its ID and start record kept for the next run to reattach. Safe to call twice.</summary>
    async Task LeaveInstanceRunningAsync()
    {
        await InstanceLock.WaitAsync(CancellationToken.None);
        try
        {
            if (ChildBackend is not null)
            {
                BackendHandler.BackendData child = ChildBackend;
                ChildBackend = null;
                await CloudChildBackend.DetachAsync(Handler, child, Provider?.ProviderName);
                await OnChildDetachedAsync();
            }
            if (Provider is not null && HasBillableInstance)
            {
                Logs.Warning($"[{Provider.ProviderName}] Leaving instance '{CurrentInstance?.InstanceId ?? Provider.ActiveInstanceId}' running (Keep Running On Shutdown). It bills until it is stopped; the next SwarmUI run reattaches it.");
            }
            CurrentInstance = null;
            InstanceStartedAt = null;
        }
        finally
        {
            InstanceLock.Release();
        }
    }

    /// <inheritdoc/>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        input.RefusalReasons.Add($"{Provider?.ProviderName ?? "Cloud instance"} control backends do not generate directly; their attached cloud Swarm backend does.");
        return false;
    }

    /// <inheritdoc/>
    public override Task<Image[]> Generate(T2IParamInput user_input)
    {
        throw new NotImplementedException("Cloud instance control backends do not generate; the attached Swarm backend does.");
    }

    /// <inheritdoc/>
    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        throw new NotImplementedException("Cloud instance control backends do not load models; the attached Swarm backend does.");
    }
}
