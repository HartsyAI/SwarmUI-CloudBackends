using FreneticUtilities.FreneticDataSyntax;
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
public abstract class CloudInstanceBackendBase : AbstractT2IBackend
{
    /// <summary>Settings every instance-renting provider shares.</summary>
    public class InstanceSettings : AutoConfiguration
    {
        [ConfigComment("How long to wait for the instance to boot and for SwarmUI on it to answer, in seconds.\nA cold start that installs SwarmUI can take many minutes.")]
        public int StartupTimeoutSec = 900;

        [ConfigComment("How often to poll the provider while waiting for the instance to start, in milliseconds.")]
        public int PollIntervalMs = 5000;

        [ConfigComment("Start the cloud instance as soon as this backend is enabled.\nIf off, the backend stays idle and the instance is only started when you press Start on it.")]
        public bool StartOnEnable = true;
    }

    /// <summary>Returns the subclass's settings cast to <see cref="InstanceSettings"/>.</summary>
    public abstract InstanceSettings InstanceConfig { get; }

    /// <summary>Factory: build a provider using the given API key.</summary>
    protected abstract ICloudInstanceProvider CreateProvider(string apiKey);

    /// <summary>Retrieve the provider API key for <paramref name="session"/>. Throw a readable error if missing.</summary>
    protected abstract string GetApiKey(Session session);

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

    public override async Task Init()
    {
        AddLoadStatus($"Starting {GetType().Name} backend...");
        Session session;
        string apiKey;
        try
        {
            CheckRequiredConfig();
            session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
            apiKey = GetApiKey(session);
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {ex.Message}");
            return;
        }
        Provider = CreateProvider(apiKey);
        try
        {
            AddLoadStatus($"Validating {Provider.ProviderName} credentials and configuration...");
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            await Provider.ValidateAsync(cancel.Token);
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {Provider.ProviderName} validation failed: {ex.Message}");
            return;
        }
        // A control instance must not be offered for generation itself.
        CanLoadModels = false;
        MaxUsages = 1;
        if (!InstanceConfig.StartOnEnable)
        {
            Status = BackendStatus.RUNNING;
            AddLoadStatus($"{Provider.ProviderName} backend ready. The instance is not started; it will start on demand.");
            return;
        }
        try
        {
            await StartInstanceAsync();
            Status = BackendStatus.RUNNING;
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {ex.Message}");
            Logs.Error($"[{Provider?.ProviderName}] Failed to start cloud instance: {ex.ReadableString()}");
        }
    }

    /// <summary>
    /// Starts the cloud instance if it is not already running, and attaches a SwarmSwarmBackend child
    /// to it. Safe to call repeatedly.
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
            AddLoadStatus($"Starting {Provider.ProviderName} instance (up to {InstanceConfig.StartupTimeoutSec}s)...");
            CurrentInstance = await Provider.StartInstanceAsync(InstanceConfig.StartupTimeoutSec, InstanceConfig.PollIntervalMs, Program.GlobalProgramCancel);
            AddLoadStatus($"Instance '{CurrentInstance.InstanceId}' is up at {CurrentInstance.PublicUrl}, attaching Swarm backend...");
            AttachChildBackend();
            AddLoadStatus($"{Provider.ProviderName} instance ready.");
        }
        finally { InstanceLock.Release(); }
    }

    /// <summary>
    /// Hands the instance URL to core's SwarmSwarmBackend as a non-real child. That child is a control
    /// instance in its own right: it mirrors the remote's backends, models and features, and spawns its
    /// own children which perform generation.
    /// </summary>
    void AttachChildBackend()
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
        ChildBackend = Handler.AddNewNonrealBackend(Handler.SwarmBackendType, BackendData, settings, newData =>
        {
            SwarmSwarmBackend swarm = newData.AbstractBackend as SwarmSwarmBackend;
            swarm.IsSpecialControlled = true;
            swarm.CanLoadModels = false;
            swarm.Title = $"[{Provider.ProviderName} {CurrentInstance.InstanceId}] {(string.IsNullOrWhiteSpace(CurrentInstance.Description) ? "Cloud Instance" : CurrentInstance.Description)}";
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
        }
        finally { InstanceLock.Release(); }
    }

    public override async Task Shutdown()
    {
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
