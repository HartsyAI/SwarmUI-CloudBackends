using System.Collections.Concurrent;
using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// The single user-facing "Cloud Backends" entry. Never generates anything itself - on Init it spins up
/// a hidden (nonreal) child backend for each provider section the user has enabled, reusing the existing
/// <see cref="RunPodServerlessBackend"/>/<see cref="RunPodPodsBackend"/>/<see cref="VastAIBackend"/>/
/// <see cref="VastAIInstanceBackend"/> classes unchanged. On Shutdown (including a settings-edit re-Init)
/// it tears all of them down again, so the child set always matches the current toggle state.
/// </summary>
public class CloudBackendsBackend : AbstractT2IBackend
{
    public class Settings : AutoConfiguration
    {
        // ── RunPod Serverless ────────────────────────────────────────────────
        [ConfigComment("Enable a RunPod Serverless worker.")]
        public bool RunPodServerless_Enabled = false;
        [ConfigComment("Cloud endpoint identifier (RunPod serverless endpoint ID).")]
        public string RunPodServerless_EndpointId = "";
        [ConfigComment("Max parallel generation requests.")]
        public int RunPodServerless_MaxConcurrent = 10;
        [ConfigComment("Poll interval while waiting for worker startup (ms).")]
        public int RunPodServerless_PollIntervalMs = 2000;
        [ConfigComment("Max worker startup timeout (seconds).")]
        public int RunPodServerless_StartupTimeoutSec = 800;
        [ConfigComment("Per-generation timeout (seconds).")]
        public int RunPodServerless_GenerationTimeoutSec = 300;
        [ConfigComment("How long to keep a woken worker alive after each request (seconds).")]
        public int RunPodServerless_KeepaliveSeconds = 420;

        // ── RunPod GPU Pods ──────────────────────────────────────────────────
        [ConfigComment("Enable a RunPod GPU Pod.")]
        public bool RunPodPods_Enabled = false;
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Existing RunPod pod ID to use.\nLeave blank to have Swarm find a pod by name, or create one.")]
        public string RunPodPods_PodId = "";
        [ConfigComment("Port SwarmUI listens on inside the pod.")]
        public int RunPodPods_SwarmUIPort = 7801;
        [ConfigComment("Destroy the pod when this backend shuts down, instead of just stopping it.")]
        public bool RunPodPods_TerminateOnShutdown = false;
        [SuggestionPlaceholder(Text = "blank = a name unique to this backend")]
        [ConfigComment("Name given to pods this backend creates, and used to find that pod again later.\nLeave blank to use a name unique to this backend (recommended) - a shared literal name here across two or more Cloud Backends instances would make each backend's 'find an existing pod by name' step liable to attach to the other's pod instead of creating its own.")]
        public string RunPodPods_PodName = "";
        [SuggestionPlaceholder(Text = "docker image with SwarmUI")]
        [ConfigComment("Docker image to create the pod from. Ignored when TemplateId is set.")]
        public string RunPodPods_ImageName = "";
        [SuggestionPlaceholder(Text = "RunPod template id")]
        [ConfigComment("RunPod template to create the pod from, instead of naming an image directly.")]
        public string RunPodPods_TemplateId = "";
        [SuggestionPlaceholder(Text = "pick a GPU, or leave blank for cheapest available")]
        [ConfigComment("GPU type for created pods. Leave blank to use whatever is available, cheapest first.")]
        public string RunPodPods_GpuTypeId = "";
        [ManualSettingsOptions(Vals = ["1", "2", "4", "8"])]
        [ConfigComment("Number of GPUs attached to a created pod.")]
        public int RunPodPods_GpuCount = 1;
        [ConfigComment("Container disk size in GB for a created pod.")]
        public int RunPodPods_ContainerDiskGb = 50;
        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("Network volume to attach, which is normally where SwarmUI and your models live.")]
        public string RunPodPods_NetworkVolumeId = "";
        [ConfigComment("Path the volume is mounted at inside the pod.")]
        public string RunPodPods_VolumeMountPath = "/runpod-volume";
        [ConfigComment("Pod volume size in GB, used only when no network volume is attached. Zero for none.")]
        public int RunPodPods_VolumeGb = 0;
        [ManualSettingsOptions(Vals = ["SECURE", "COMMUNITY"], ManualNames = ["Secure Cloud", "Community Cloud"])]
        [ConfigComment("Which RunPod cloud to create pods in. Secure is more reliable, Community is cheaper.")]
        public string RunPodPods_CloudType = "SECURE";
        [SuggestionPlaceholder(Text = "leave blank to let RunPod choose")]
        [ConfigComment("Restrict created pods to one data center. Ignored when a network volume is attached.")]
        public string RunPodPods_DataCenterId = "";
        [ConfigComment("Environment variables for a created pod, as KEY=VALUE, one per line.")]
        public string RunPodPods_PodEnv = "";
        [ConfigComment("How long to wait for the pod to boot and for SwarmUI on it to answer, in seconds.")]
        public int RunPodPods_StartupTimeoutSec = 900;
        [ConfigComment("How often to poll RunPod while waiting for the pod to start, in milliseconds.")]
        public int RunPodPods_PollIntervalMs = 5000;
        [ConfigComment("Failsafe: auto-stop the pod after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int RunPodPods_MaxRuntimeMinutes = 0;
        [ConfigComment("Failsafe: auto-stop the pod once its estimated spend (hourly rate x time running) reaches this many US dollars. Zero disables this check.")]
        public double RunPodPods_MaxSpendUsd = 0;

        // ── Vast.ai Serverless ───────────────────────────────────────────────
        [ConfigComment("Enable a Vast.ai Serverless worker.")]
        public bool VastAI_Enabled = false;
        [ConfigComment("Vast.ai serverless endpoint NAME (matches on /route/).")]
        public string VastAI_EndpointId = "";
        [SuggestionPlaceholder(Text = "handler")]
        [ConfigComment("Route on the worker that serves the SwarmUI wakeup handler.")]
        public string VastAI_WorkerRoute = "handler";
        [ConfigComment("Max parallel generation requests.")]
        public int VastAI_MaxConcurrent = 10;
        [ConfigComment("Poll interval while waiting for worker startup (ms).")]
        public int VastAI_PollIntervalMs = 2000;
        [ConfigComment("Max worker startup timeout (seconds).")]
        public int VastAI_StartupTimeoutSec = 800;
        [ConfigComment("Per-generation timeout (seconds).")]
        public int VastAI_GenerationTimeoutSec = 300;
        [ConfigComment("How long to keep a woken worker alive after each request (seconds).")]
        public int VastAI_KeepaliveSeconds = 420;

        // ── Vast.ai Instances ────────────────────────────────────────────────
        // Prefixed VastAIInstance_ (not VastAI_, which the Serverless section above already owns).
        [ConfigComment("Enable a Vast.ai rented instance.")]
        public bool VastAIInstance_Enabled = false;
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Existing Vast.ai instance ID to use.\nLeave blank to have Swarm find an instance by label, or create one.")]
        public string VastAIInstance_InstanceId = "";
        [ConfigComment("Port SwarmUI listens on inside the instance.\nDeclared to Vast as a Docker '-p' flag, so the instance's own SwarmUI must actually bind this port.")]
        public int VastAIInstance_SwarmUIPort = 7801;
        [ConfigComment("Destroy the instance when this backend shuts down, instead of just stopping it.")]
        public bool VastAIInstance_TerminateOnShutdown = false;
        [SuggestionPlaceholder(Text = "blank = a label unique to this backend")]
        [ConfigComment("Label given to instances this backend creates, and used to find that instance again later.\nLeave blank to use a label unique to this backend (recommended) - a shared literal label across two or more Cloud Backends instances would make each backend's find-by-label step liable to attach to the other's instance instead of creating its own.")]
        public string VastAIInstance_Label = "";
        [SuggestionPlaceholder(Text = "docker image with SwarmUI")]
        [ConfigComment("Docker image to create the instance from. Ignored when TemplateHashId is set.")]
        public string VastAIInstance_Image = "";
        [SuggestionPlaceholder(Text = "Vast.ai template hash")]
        [ConfigComment("Vast.ai template to create the instance from, instead of naming an image directly.")]
        public string VastAIInstance_TemplateHashId = "";
        [SuggestionPlaceholder(Text = "pick an offer, or leave blank for cheapest available")]
        [ConfigComment("Specific rentable offer to create the instance from.\nLeave blank to search on-demand offers and take the cheapest match. Unlike RunPod's GPU-type retry, an offer is a specific host slot - if it's gone by the time Swarm tries to use it, pick another rather than expecting an automatic fallback.")]
        public string VastAIInstance_OfferId = "";
        [ConfigComment("Container disk size in GB for a created instance. This is wiped when the instance is destroyed.")]
        public int VastAIInstance_DiskGb = 20;
        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("Existing network volume to attach, which is normally where SwarmUI and your models live.\nCreating a brand new named volume isn't supported here yet - attach one you already created on Vast.ai.")]
        public string VastAIInstance_NetworkVolumeId = "";
        [ConfigComment("Path the volume is mounted at inside the instance.")]
        public string VastAIInstance_VolumeMountPath = "/workspace";
        [ConfigComment("Extra environment variables for a created instance, as KEY=VALUE, one per line.\nThe SwarmUI port mapping is added automatically - no need to include it here.")]
        public string VastAIInstance_Env = "";
        [ConfigComment("How long to wait for the instance to boot and for SwarmUI on it to answer, in seconds.")]
        public int VastAIInstance_StartupTimeoutSec = 900;
        [ConfigComment("How often to poll Vast.ai while waiting for the instance to start, in milliseconds.")]
        public int VastAIInstance_PollIntervalMs = 5000;
        [ConfigComment("Failsafe: auto-stop the instance after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int VastAIInstance_MaxRuntimeMinutes = 0;
        [ConfigComment("Failsafe: auto-stop the instance once its estimated spend (hourly rate x time running) reaches this many US dollars. Zero disables this check.")]
        public double VastAIInstance_MaxSpendUsd = 0;
    }

    Settings Config => (Settings)SettingsRaw;

    /// <summary>One provider section of the parent settings: its field prefix, display label, which API key it bills, whether it rents whole instances, and its hidden backend type.</summary>
    public record ProviderDef(string Prefix, string Label, string KeyType, bool IsInstance, Func<BackendHandler.BackendType> Type);

    /// <summary>Every provider section this backend can manage, in settings-declaration order.</summary>
    public static readonly ProviderDef[] Providers =
    [
        new("RunPodServerless_", "RunPod Serverless", "runpod_api", IsInstance: false, () => CloudBackendTypes.RunPodServerless),
        new("RunPodPods_", "RunPod GPU Pods", "runpod_api", IsInstance: true, () => CloudBackendTypes.RunPodPods),
        new("VastAI_", "Vast.ai Serverless", "vastai_api", IsInstance: false, () => CloudBackendTypes.VastAI),
        new("VastAIInstance_", "Vast.ai Instances", "vastai_api", IsInstance: true, () => CloudBackendTypes.VastAIInstance)
    ];

    /// <summary>Looks up a provider definition by its settings prefix (e.g. "RunPodPods_").</summary>
    public static ProviderDef ProviderByPrefix(string prefix) => Providers.First(d => d.Prefix == prefix);

    /// <summary>True if the given provider section is enabled in this backend's settings.</summary>
    public bool IsProviderEnabled(ProviderDef def) => def.Prefix switch
    {
        "RunPodServerless_" => Config.RunPodServerless_Enabled,
        "RunPodPods_" => Config.RunPodPods_Enabled,
        "VastAI_" => Config.VastAI_Enabled,
        "VastAIInstance_" => Config.VastAIInstance_Enabled,
        _ => false
    };

    /// <summary>Child backend IDs per "{userId}/{prefix}", so each user gets exactly one child per enabled provider.</summary>
    readonly ConcurrentDictionary<string, int> ChildMap = new();

    /// <summary>Per-(user, provider) spawn locks, so concurrent first requests cannot double-spawn a child.</summary>
    readonly ConcurrentDictionary<string, SemaphoreSlim> SpawnLocks = new();

    /// <summary>Control backend only - never loads models or generates directly.</summary>
    public override IEnumerable<string> SupportedFeatures => [];

    /// <summary>
    /// Copies this backend's <c>{prefix}X</c> settings values into a child settings object's matching
    /// <c>X</c> fields, via FDS save/load, so a provider's fields only have to be declared (with the
    /// prefix) in <see cref="Settings"/> and never hand-copied per field. The <c>{prefix}Enabled</c>
    /// toggle is skipped - it belongs to this parent, not the child.
    /// </summary>
    AutoConfiguration BuildChildSettings(ProviderDef def)
    {
        FDSSection all = SettingsRaw.Save(true);
        FDSSection stripped = new();
        foreach (string key in all.GetRootKeys().Where(k => k.StartsWith(def.Prefix) && k != $"{def.Prefix}Enabled"))
        {
            stripped.Set(key[def.Prefix.Length..], all.GetObject(key));
        }
        AutoConfiguration child = Activator.CreateInstance(def.Type().SettingsClass) as AutoConfiguration;
        child.Load(stripped);
        return child;
    }

    public override async Task Init()
    {
        CanLoadModels = false;
        MaxUsages = 1;
        // Children are per-user and spawn on demand (each runs on its owner's own API key), so a
        // settings re-Init only needs to drop the old set; users get fresh children on next use.
        // Note this stops any user's running instance children - core backend edits always restart
        // the backend tree they configure.
        await TearDownChildren();
        int enabledCount = Providers.Count(IsProviderEnabled);
        Status = BackendStatus.RUNNING;
        AddLoadStatus(enabledCount is 0
            ? "No provider enabled - nothing available. Expand a provider section, enable it, and Save."
            : $"{enabledCount} provider(s) enabled. Per-user backends are created on demand for each user with an API key set.");
    }

    /// <summary>Returns the given user's existing child for a provider, in any status, or null.</summary>
    public AbstractT2IBackend GetChildFor(string userId, ProviderDef def)
    {
        if (ChildMap.TryGetValue($"{userId}/{def.Prefix}", out int id) && Program.Backends.AllBackends.TryGetValue(id, out BackendHandler.BackendData data))
        {
            return data.AbstractBackend as AbstractT2IBackend;
        }
        return null;
    }

    /// <summary>
    /// Ensures the given user has a child backend for the given provider, spawning one on their own API
    /// key if needed. Idempotent and cheap when the child already exists (any status - an ERRORED child
    /// is only respawned when the user's key has changed since it errored, so a bad key cannot become a
    /// per-generation retry loop). Returns null without side effects when the section is disabled or
    /// the user has no key; <paramref name="throwWhenUnavailable"/> upgrades those to readable errors
    /// for explicit user actions.
    ///
    /// Cost safety: a spawned serverless child never auto-refreshes models (that wakes a billed worker;
    /// the explicit refresh route covers it), and a spawned instance child never auto-starts its
    /// instance (the explicit start route covers it) - a user generating on some other backend must
    /// never be billed as a side effect.
    /// </summary>
    public async Task<AbstractT2IBackend> EnsureChildForUser(User user, ProviderDef def, bool throwWhenUnavailable = false)
    {
        if (user is null)
        {
            return null;
        }
        if (!IsProviderEnabled(def))
        {
            if (throwWhenUnavailable)
            {
                throw new SwarmReadableErrorException($"The {def.Label} section of Cloud Backends #{BackendData.ID} is not enabled.");
            }
            return null;
        }
        string key = $"{user.UserID}/{def.Prefix}";
        string apiKey = user.GetGenericData(def.KeyType, "key")?.Trim();
        AbstractT2IBackend existing = GetChildFor(user.UserID, def);
        if (existing is not null && !(existing.Status == BackendStatus.ERRORED && existing is ICloudBackend cloud && !string.IsNullOrEmpty(apiKey) && !cloud.IsUsingApiKey(apiKey)))
        {
            return existing;
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            if (throwWhenUnavailable)
            {
                throw new SwarmReadableErrorException($"No {def.Label} API key on file for user '{user.UserID}'. Set it in User Settings, API Keys - it is never borrowed from another user.");
            }
            return null;
        }
        SemaphoreSlim spawnLock = SpawnLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await spawnLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            existing = GetChildFor(user.UserID, def);
            if (existing is not null)
            {
                if (existing.Status == BackendStatus.ERRORED && existing is ICloudBackend cloud2 && !cloud2.IsUsingApiKey(apiKey))
                {
                    Logs.Info($"[CloudBackends] Respawning errored {def.Label} child for user '{user.UserID}' after an API key change.");
                    await RemoveChild(existing.BackendData.ID, key);
                }
                else
                {
                    return existing;
                }
            }
            AutoConfiguration settings = BuildChildSettings(def);
            if (settings is RunPodPodsBackend.Settings pods && string.IsNullOrWhiteSpace(pods.PodName))
            {
                // A name unique to this backend AND user, so each user's find-by-name reattaches their
                // own pod. The local user keeps the pre-per-user default name, so upgrading does not
                // orphan (and keep billing) a pod created under the old naming.
                pods.PodName = user.UserID == SessionHandler.LocalUserID ? $"swarmui-cloudbackends-{BackendData.ID}" : $"swarmui-cloudbackends-{BackendData.ID}-{user.UserID}";
            }
            if (settings is VastAIInstanceBackend.Settings vast && string.IsNullOrWhiteSpace(vast.Label))
            {
                // Same unique fallback as the pod name above, for the same reasons.
                vast.Label = user.UserID == SessionHandler.LocalUserID ? $"swarmui-cloudbackends-{BackendData.ID}" : $"swarmui-cloudbackends-{BackendData.ID}-{user.UserID}";
            }
            Logs.Info($"[CloudBackends] Spawning {def.Label} child of backend #{BackendData.ID} for user '{user.UserID}'.");
            BackendHandler.BackendData data = Handler.AddNewNonrealBackend(def.Type(), BackendData, settings, newData =>
            {
                newData.AbstractBackend.Title = $"[Cloud Backends #{BackendData.ID}] {def.Label} ({user.UserID})";
                switch (newData.AbstractBackend)
                {
                    case CloudBackendBase c: c.OwnerUserId = user.UserID; break;
                    case CloudInstanceBackendBase i: i.OwnerUserId = user.UserID; break;
                }
                // AddNewNonrealBackend takes a `parent` argument but (as of this writing) never assigns
                // it - set it ourselves so WebAPI calls can find "user X's RunPod Pods child of backend
                // #N" by the one ID the frontend actually has (the parent's), instead of needing the
                // child's own hidden negative ID, which is never exposed to the UI.
                newData.AbstractParent = BackendData;
            });
            ChildMap[key] = data.ID;
            return data.AbstractBackend as AbstractT2IBackend;
        }
        finally { spawnLock.Release(); }
    }

    /// <summary>
    /// Waits for a just-spawned child to finish Init. The bound stays short deliberately: this only
    /// ever gates child Init (credential validation, capped at 30s) - a cold instance boot happens
    /// later inside explicit start calls, never here.
    /// </summary>
    public static async Task WaitForChildReady(AbstractT2IBackend child, int timeoutSec = 60)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            switch (child.Status)
            {
                case BackendStatus.RUNNING:
                case BackendStatus.IDLE:
                    return;
                case BackendStatus.ERRORED:
                case BackendStatus.DISABLED:
                    string lastStatus = child.LoadStatusReport?.LastOrDefault()?.Message;
                    throw new SwarmReadableErrorException($"Cloud backend failed to start.{(string.IsNullOrWhiteSpace(lastStatus) ? "" : $" {lastStatus}")}");
            }
            await Task.Delay(250, Program.GlobalProgramCancel);
        }
        throw new SwarmReadableErrorException($"Cloud backend did not finish starting within {timeoutSec}s.");
    }

    /// <summary>Deletes one child and forgets it. Must be called under the child's spawn lock.</summary>
    async Task RemoveChild(int id, string mapKey)
    {
        ChildMap.TryRemove(mapKey, out _);
        try { await Handler.DeleteById(id); }
        catch (Exception ex) { Logs.Debug($"[CloudBackends] Removing child backend #{id} failed: {ex.Message}"); }
    }

    async Task TearDownChildren()
    {
        foreach (KeyValuePair<string, int> pair in ChildMap)
        {
            ChildMap.TryRemove(pair.Key, out _);
            try { await Handler.DeleteById(pair.Value); }
            catch (Exception ex) { Logs.Debug($"[CloudBackends] Removing child backend #{pair.Value} failed: {ex.Message}"); }
        }
    }

    public override async Task Shutdown()
    {
        await TearDownChildren();
        Status = BackendStatus.DISABLED;
    }

    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        input.RefusalReasons.Add("Cloud Backends is a control backend; its enabled provider(s) generate, not this entry directly.");
        return false;
    }

    public override Task<Image[]> Generate(T2IParamInput user_input) =>
        throw new NotImplementedException("Cloud Backends is a control backend; it does not generate directly.");

    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input) =>
        throw new NotImplementedException("Cloud Backends is a control backend; it does not load models directly.");
}
