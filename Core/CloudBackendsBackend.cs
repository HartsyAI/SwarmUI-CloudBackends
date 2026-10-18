using System.Collections.Concurrent;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
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
/// The single user-facing "Cloud Backends" entry. Never generates itself: each user gets hidden (nonreal) children, one
/// per enabled provider section, created on demand on that user's own API key. Shutdown (including a settings edit) tears
/// them all down; users get fresh ones on next use.
/// </summary>
public class CloudBackendsBackend : AbstractT2IBackend
{
    public class Settings : AutoConfiguration
    {
        // ── RunPod Serverless ────────────────────────────────────────────────
        [ConfigComment("Enable RunPod Serverless.")]
        public bool RunPodServerless_Enabled = false;
        [ConfigComment("RunPod serverless endpoint ID.")]
        public string RunPodServerless_EndpointId = "";
        [ConfigComment("Most workers this backend may run at once. When every running worker is busy, another one is started, up to this many.\nAlso limited by the endpoint's own maximum. 1 means never more than one worker.")]
        public int RunPodServerless_MaxWorkers = 1;
        [ConfigComment("How long a worker may sit with no generation before it shuts down, in seconds.\nLower costs less; higher avoids a cold start between bursts of work.")]
        public int RunPodServerless_IdleSeconds = 120;
        [ConfigComment("Longest a single worker lease may last, in seconds. The endpoint's execution timeout must be longer.\nA worker still in use when its lease ends is simply leased again.")]
        public int RunPodServerless_MaxLeaseSeconds = 3600;
        [ConfigComment("How long to wait for a worker to start and answer, in seconds.")]
        public int RunPodServerless_StartupTimeoutSec = 800;
        [ConfigComment("How often to poll while waiting for a worker, in milliseconds.")]
        public int RunPodServerless_PollIntervalMs = 2000;

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
        [ConfigComment("Name given to pods this backend creates, and used to find that pod again later.\nLeave blank (recommended) for a name unique to this backend and user.")]
        public string RunPodPods_PodName = "";
        [ConfigComment("Docker image to create the pod from. Ignored when TemplateId is set.\nUse the Hartsy RunPod worker image (pinned to a release), which secures the pod with a token this backend manages.")]
        public string RunPodPods_ImageName = CloudBackendTypes.DefaultRunPodImage;
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
        [ConfigComment("Only for a pod you created yourself (set in Pod ID) that already runs the Hartsy worker: its SWARMUI_WORKER_TOKEN.\nLeave blank to let Cloud Backends create the pod with a token of its own. Hidden once saved.")]
        [ValueIsSecret]
        public string RunPodPods_WorkerToken = "";
        [ConfigComment("How long to wait for the pod to boot and for SwarmUI on it to answer, in seconds.")]
        public int RunPodPods_StartupTimeoutSec = 900;
        [ConfigComment("How often to poll RunPod while waiting for the pod to start, in milliseconds.")]
        public int RunPodPods_PollIntervalMs = 5000;
        [ConfigComment("Failsafe: auto-stop the pod after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int RunPodPods_MaxRuntimeMinutes = 0;
        [ConfigComment("Failsafe: auto-stop the pod once its estimated spend (hourly rate x time running) reaches this many US dollars. Zero disables this check.")]
        public double RunPodPods_MaxSpendUsd = 0;

        // ── Vast.ai Serverless ───────────────────────────────────────────────
        [ConfigComment("Enable Vast.ai Serverless.")]
        public bool VastAI_Enabled = false;
        [ConfigComment("Vast.ai serverless endpoint name.")]
        public string VastAI_EndpointId = "";
        [ConfigComment("Most workers this backend may run at once. When every running worker is busy, another one is started, up to this many.\nAlso limited by the endpoint's own maximum. 1 means never more than one worker.")]
        public int VastAI_MaxWorkers = 1;
        [ConfigComment("How long a worker may sit with no generation before it shuts down, in seconds.\nLower costs less; higher avoids a cold start between bursts of work.\nVast.ai workers are kept at least 60 seconds, so a session cannot lapse between renewals.")]
        public int VastAI_IdleSeconds = 120;
        [ConfigComment("How long to wait for a worker to start and answer, in seconds.")]
        public int VastAI_StartupTimeoutSec = 800;
        [ConfigComment("How often to poll while waiting for a worker, in milliseconds.")]
        public int VastAI_PollIntervalMs = 2000;

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
        [ConfigComment("Label given to instances this backend creates, and used to find that instance again later.\nLeave blank (recommended) for a label unique to this backend and user.")]
        public string VastAIInstance_Label = "";
        [ConfigComment("Docker image to create the instance from. Ignored when TemplateHashId is set.\nUse the Hartsy Vast.ai worker image (pinned to a release), which serves HTTPS and is secured with a token this backend manages.")]
        public string VastAIInstance_Image = CloudBackendTypes.DefaultVastImage;
        [SuggestionPlaceholder(Text = "Vast.ai template hash")]
        [ConfigComment("Vast.ai template to create the instance from, instead of naming an image directly.")]
        public string VastAIInstance_TemplateHashId = "";
        [SuggestionPlaceholder(Text = "pick an offer, or leave blank for cheapest available")]
        [ConfigComment("Specific rentable offer (machine) to create the instance from.\nLeave blank to take the cheapest matching offer. If a chosen offer is gone by the time you start, pick another.")]
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
        [ConfigComment("Only for an instance you created yourself (set in Instance ID) that already runs the Hartsy worker: its SWARMUI_WORKER_TOKEN.\nLeave blank to let Cloud Backends create the instance with a token of its own. Hidden once saved.")]
        [ValueIsSecret]
        public string VastAIInstance_WorkerToken = "";
        [ConfigComment("How long to wait for the instance to boot and for SwarmUI on it to answer, in seconds.")]
        public int VastAIInstance_StartupTimeoutSec = 900;
        [ConfigComment("How often to poll Vast.ai while waiting for the instance to start, in milliseconds.")]
        public int VastAIInstance_PollIntervalMs = 5000;
        [ConfigComment("Failsafe: auto-stop the instance after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int VastAIInstance_MaxRuntimeMinutes = 0;
        [ConfigComment("Failsafe: auto-stop the instance once its estimated spend (hourly rate x time running) reaches this many US dollars. Zero disables this check.")]
        public double VastAIInstance_MaxSpendUsd = 0;
    }

    /// <summary>This backend's settings.</summary>
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

    /// <summary>When each errored child was first seen errored, so a transient failure is retried after a while.</summary>
    readonly ConcurrentDictionary<string, DateTime> ErroredSince = new();

    /// <summary>How long an errored child is kept before the next use respawns it.</summary>
    static readonly TimeSpan ErroredRetryAfter = TimeSpan.FromMinutes(5);

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
        WarmUsersWithKeys();
    }

    /// <summary>
    /// Creates, in the background, the serverless backends of every user who already has that provider's API key, which
    /// loads their saved cloud model lists. Core checks a model name before any backend sees a request, so without this a
    /// cloud-only model is refused after a restart until something else wakes the user's backend. Leases nothing, and
    /// instance sections are left alone because starting one bills.
    /// </summary>
    void WarmUsersWithKeys()
    {
        foreach (ProviderDef def in Providers.Where(d => !d.IsInstance && IsProviderEnabled(d)))
        {
            foreach (string userId in UsersWithKey(def.KeyType))
            {
                User user = Program.Sessions.GetUser(userId, makeNew: false);
                if (user is not null)
                {
                    _ = Utilities.RunCheckedTask(() => EnsureChildForUser(user, def), "cloud backends startup provisioning");
                }
            }
        }
    }

    /// <summary>IDs of users who have saved an API key of the given type.</summary>
    static List<string> UsersWithKey(string keyType)
    {
        // Core stores it as generic data '{user}///${keyType}///key'.
        string suffix = $"///${keyType}///key";
        lock (Program.Sessions.DBLock)
        {
            return [.. Program.Sessions.GenericData.FindAll().Select(g => g.ID).Where(id => id.EndsWith(suffix)).Select(id => id.Before("///")).Distinct()];
        }
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
    /// key if needed. Idempotent and cheap when the child already exists. An errored child is respawned
    /// only after the user's key changes or a few minutes pass, so a bad key cannot become a retry loop. Returns null without side effects when the section is disabled or
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
        if (existing is not null && !ShouldRespawn(existing, key, apiKey))
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
                if (!ShouldRespawn(existing, key, apiKey))
                {
                    return existing;
                }
                Logs.Info($"[CloudBackends] Respawning errored {def.Label} child for user '{user.UserID}'.");
                ErroredSince.TryRemove(key, out _);
                await RemoveChild(existing.BackendData.ID, key);
            }
            AutoConfiguration settings = BuildChildSettings(def);
            // A name unique to this backend and user, so each user's find-by-name reattaches only their own machine. The local
            // user keeps the original name, so a machine created before per-user names is still found instead of rented again.
            string uniqueName = user.UserID == SessionHandler.LocalUserID ? $"swarmui-cloudbackends-{BackendData.ID}" : $"swarmui-cloudbackends-{BackendData.ID}-{user.UserID}";
            if (settings is RunPodPodsBackend.Settings pods && string.IsNullOrWhiteSpace(pods.PodName))
            {
                pods.PodName = uniqueName;
            }
            if (settings is VastAIInstanceBackend.Settings vast && string.IsNullOrWhiteSpace(vast.Label))
            {
                vast.Label = uniqueName;
            }
            Logs.Info($"[CloudBackends] Spawning {def.Label} child of backend #{BackendData.ID} for user '{user.UserID}'.");
            BackendHandler.BackendData data = Handler.AddNewNonrealBackend(def.Type(), BackendData, settings, newData =>
            {
                newData.AbstractBackend.Title = $"[Cloud Backends #{BackendData.ID}] {def.Label} ({user.UserID})";
                if (newData.AbstractBackend is CloudBackendBase serverless)
                {
                    serverless.OwnerUserId = user.UserID;
                }
                else if (newData.AbstractBackend is CloudInstanceBackendBase instance)
                {
                    instance.OwnerUserId = user.UserID;
                }
                // Core never assigns the parent of a nonreal backend; the routes find a user's child through it.
                newData.AbstractParent = BackendData;
            });
            ChildMap[key] = data.ID;
            if (Status is BackendStatus.DISABLED or BackendStatus.ERRORED)
            {
                // This backend shut down while the child was being created; nothing would ever manage it.
                await RemoveChild(data.ID, key);
                return null;
            }
            return data.AbstractBackend as AbstractT2IBackend;
        }
        finally
        {
            spawnLock.Release();
        }
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
        try
        {
            await Handler.DeleteById(id);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[CloudBackends] Removing child backend #{id} failed: {ex.Message}");
        }
    }

    /// <summary>Deletes every child.</summary>
    async Task TearDownChildren()
    {
        foreach (KeyValuePair<string, int> pair in ChildMap)
        {
            await RemoveChild(pair.Value, pair.Key);
        }
        ErroredSince.Clear();
    }

    /// <summary>True if an existing child should be replaced: it errored, and either the owner's key changed since, or it has stayed errored a while.</summary>
    bool ShouldRespawn(AbstractT2IBackend existing, string key, string apiKey)
    {
        if (existing.Status != BackendStatus.ERRORED || string.IsNullOrEmpty(apiKey))
        {
            ErroredSince.TryRemove(key, out _);
            return false;
        }
        if (existing is ICloudBackend cloud && !cloud.IsUsingApiKey(apiKey))
        {
            return true;
        }
        DateTime since = ErroredSince.GetOrAdd(key, _ => DateTime.UtcNow);
        return DateTime.UtcNow - since > ErroredRetryAfter;
    }

    public override async Task Shutdown()
    {
        // Set first, so a child still being created sees it and removes itself.
        Status = BackendStatus.DISABLED;
        await TearDownChildren();
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
