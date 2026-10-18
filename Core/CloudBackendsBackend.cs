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
    /// <summary>
    /// Every setting the card shows. The extension's script groups them into one section per provider by prefix, gives them
    /// plain labels, and moves the fields in <see cref="AdvancedFields"/> into a collapsed Advanced group.
    /// Each child backend gets its section's values copied in by name (see <see cref="BuildChildSettings"/>).
    /// </summary>
    public class Settings : AutoConfiguration
    {
        // ── RunPod Serverless ────────────────────────────────────────────────
        [ConfigComment("Turn RunPod Serverless on or off.")]
        public bool RunPodServerless_Enabled = false;
        [SuggestionPlaceholder(Text = "pick an endpoint, or paste its ID")]
        [ConfigComment("Your RunPod serverless endpoint.\nPick it from the list (filled from your RunPod account once your API key is saved),\nor paste the endpoint ID from RunPod's Serverless page.")]
        public string RunPodServerless_EndpointId = "";
        [ManualSettingsOptions(Vals = ["1", "2", "3", "4", "5", "6", "8", "10"])]
        [ConfigComment("Most workers SwarmUI may run at once.\nWhen every running worker is busy, another one starts, up to this many.\nThe endpoint's own Max Workers must be at least this high.")]
        public int RunPodServerless_MaxWorkers = 1;
        [ManualSettingsOptions(Vals = ["30", "60", "120", "300", "600", "900", "1800"], ManualNames = ["30 seconds", "1 minute", "2 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes"])]
        [ConfigComment("How long a worker waits with nothing to do before it shuts down.\nShorter costs less; longer avoids waiting for a new worker between bursts.")]
        public int RunPodServerless_IdleSeconds = 120;
        [ManualSettingsOptions(Vals = ["300", "600", "900", "1200", "1800"], ManualNames = ["5 minutes", "10 minutes", "15 minutes", "20 minutes", "30 minutes"])]
        [ConfigComment("How long to wait for a worker to start before giving up.\nRaise it if a new or large worker image takes long to download.")]
        public int RunPodServerless_StartupTimeoutSec = 900;

        // ── RunPod GPU Pods ──────────────────────────────────────────────────
        [ConfigComment("Turn RunPod GPU Pods on or off.")]
        public bool RunPodPods_Enabled = false;
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Which pod to use.\nLeave on '(create one for me)' and Start Pod creates a pod, and finds it again next time.\nOr pick a pod you already made (it must run the Hartsy RunPod worker, see Worker Token).")]
        public string RunPodPods_PodId = "";
        [ConfigComment("Only for a pod you made yourself (shown once you pick one under Pod): the SWARMUI_WORKER_TOKEN it was created with.\nThe card makes its own token for pods it creates. Hidden once saved.")]
        [ValueIsSecret]
        public string RunPodPods_WorkerToken = "";
        [ConfigComment("What Stop Pod does.\nOn: deletes the pod. Your models stay on the network volume, and the next Start makes a fresh pod.\nOff: pauses the pod. RunPod clears its container disk anyway, and a paused pod can come back without a GPU.")]
        public bool RunPodPods_DeleteOnStop = true;
        [SuggestionPlaceholder(Text = "pick an image, or type your own")]
        [ConfigComment("The Docker image the pod runs.\nUse the Hartsy RunPod worker: pick the ComfyUI or HartsyInference version from the list.\nIgnored when a Template is set (under Advanced).")]
        public string RunPodPods_ImageName = CloudBackendTypes.DefaultRunPodImage;
        [SuggestionPlaceholder(Text = "pick a GPU, or leave blank for cheapest available")]
        [ConfigComment("The GPU to rent, with its VRAM, price per hour and stock.\n'(cheapest available)' takes the cheapest GPU that's free when you press Start.\nPick 16 GB of VRAM or more; 24 GB for large models like Flux.")]
        public string RunPodPods_GpuTypeId = "";
        [ManualSettingsOptions(Vals = ["20", "30", "50", "75", "100", "150", "200"], ManualNames = ["20 GB", "30 GB", "50 GB", "75 GB", "100 GB", "150 GB", "200 GB"])]
        [ConfigComment("Size of the pod's own disk. It holds the worker image, so it must be well above the image size:\n30 GB or more for the ComfyUI image, 20 GB for HartsyInference. It's wiped when the pod stops.")]
        public int RunPodPods_ContainerDiskGb = 50;
        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("The network volume with your models. It survives the pod being deleted.\nThe pod is created in the volume's data center.")]
        public string RunPodPods_NetworkVolumeId = "";
        [ManualSettingsOptions(Vals = ["SECURE", "COMMUNITY"], ManualNames = ["Secure Cloud", "Community Cloud"])]
        [ConfigComment("Which RunPod cloud to rent from. Secure Cloud is more reliable; Community Cloud is cheaper.\nThe GPU list shows the prices for the cloud picked here.")]
        public string RunPodPods_CloudType = "SECURE";
        [SuggestionPlaceholder(Text = "leave blank to let RunPod choose")]
        [ConfigComment("Restrict new pods to one data center.\nOnly used without a network volume, since the volume decides the data center.")]
        public string RunPodPods_DataCenterId = "";
        [ManualSettingsOptions(Vals = ["0", "30", "60", "120", "240", "480", "720", "1440"], ManualNames = ["Off", "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours", "12 hours", "24 hours"])]
        [ConfigComment("Safety stop: stops the pod after it has run this long, in case you forget it.")]
        public int RunPodPods_MaxRuntimeMinutes = 240;
        [ManualSettingsOptions(Vals = ["0", "1", "2", "5", "10", "20", "50", "100"], ManualNames = ["Off", "$1", "$2", "$5", "$10", "$20", "$50", "$100"])]
        [ConfigComment("Safety stop: stops the pod once it has cost about this much (price per hour times time running).")]
        public double RunPodPods_MaxSpendUsd = 10;
        // Advanced:
        [SuggestionPlaceholder(Text = "RunPod template id")]
        [ConfigComment("Create the pod from one of your RunPod pod templates instead of the image above.\nThe template must run the Hartsy RunPod worker.")]
        public string RunPodPods_TemplateId = "";
        [ManualSettingsOptions(Vals = ["1", "2", "4", "8"])]
        [ConfigComment("GPUs per pod. The Hartsy worker uses one GPU, so more only helps a custom image that uses them all.")]
        public int RunPodPods_GpuCount = 1;
        [ConfigComment("Extra disk, in GB, that survives a paused pod. Only used without a network volume,\nand deleted with the pod. 0 for none.")]
        public int RunPodPods_VolumeGb = 0;
        [ConfigComment("Where the network volume (or the extra disk) is mounted inside the pod.\nThe Hartsy worker finds models at /runpod-volume or /workspace; change it only for a custom image.")]
        public string RunPodPods_VolumeMountPath = "/runpod-volume";
        [ConfigComment("The port SwarmUI listens on inside the pod. The card passes it to the Hartsy worker.\nChange it only for a custom image that listens elsewhere.")]
        public int RunPodPods_SwarmUIPort = 7801;
        [ConfigComment("Extra environment variables for new pods, one KEY=VALUE per line.")]
        public string RunPodPods_PodEnv = "";
        [ManualSettingsOptions(Vals = ["300", "600", "900", "1200", "1800"], ManualNames = ["5 minutes", "10 minutes", "15 minutes", "20 minutes", "30 minutes"])]
        [ConfigComment("How long to wait for the pod to boot and SwarmUI on it to answer.\nRaise it if the first start of a large image times out.")]
        public int RunPodPods_StartupTimeoutSec = 900;

        // ── Vast.ai Serverless ───────────────────────────────────────────────
        [ConfigComment("Turn Vast.ai Serverless on or off.")]
        public bool VastAI_Enabled = false;
        [SuggestionPlaceholder(Text = "pick an endpoint, or type its name")]
        [ConfigComment("Your Vast.ai serverless endpoint.\nPick it from the list (filled from your Vast.ai account once your API key is saved),\nor type the endpoint's name from Vast's Serverless page.")]
        public string VastAI_EndpointId = "";
        [ManualSettingsOptions(Vals = ["1", "2", "3", "4", "5", "6", "8", "10"])]
        [ConfigComment("Most workers SwarmUI may run at once.\nWhen every running worker is busy, another one starts, up to this many.\nThe endpoint's own Max Workers must be at least this high.")]
        public int VastAI_MaxWorkers = 1;
        [ManualSettingsOptions(Vals = ["60", "120", "300", "600", "900", "1800"], ManualNames = ["1 minute", "2 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes"])]
        [ConfigComment("How long a worker waits with nothing to do before SwarmUI lets it go.\nShorter costs less; longer avoids waiting for a new worker between bursts.")]
        public int VastAI_IdleSeconds = 120;
        [ManualSettingsOptions(Vals = ["300", "600", "900", "1200", "1800"], ManualNames = ["5 minutes", "10 minutes", "15 minutes", "20 minutes", "30 minutes"])]
        [ConfigComment("How long to wait for a worker to start before giving up.\nRaise it if a new or large worker image takes long to download.")]
        public int VastAI_StartupTimeoutSec = 900;

        // ── Vast.ai Instances ────────────────────────────────────────────────
        // Prefixed VastAIInstance_ (not VastAI_, which the Serverless section above already owns).
        [ConfigComment("Turn Vast.ai Instances on or off.")]
        public bool VastAIInstance_Enabled = false;
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Which instance to use.\nLeave on '(create one for me)' and Start Instance rents one, and finds it again next time.\nOr pick an instance you already made (it must run the Hartsy Vast.ai worker, see Worker Token).")]
        public string VastAIInstance_InstanceId = "";
        [ConfigComment("Only for an instance you made yourself (shown once you pick one under Instance): the SWARMUI_WORKER_TOKEN it was created with.\nThe card makes its own token for instances it creates. Hidden once saved.")]
        [ValueIsSecret]
        public string VastAIInstance_WorkerToken = "";
        [ConfigComment("What Stop Instance does.\nOn: destroys the instance, so nothing bills. Keep models on a network volume.\nOff: pauses it. Its disk keeps billing, and it can only resume once that machine's GPU is free again.")]
        public bool VastAIInstance_DeleteOnStop = true;
        [SuggestionPlaceholder(Text = "pick an image, or type your own")]
        [ConfigComment("The Docker image the instance runs.\nUse the Hartsy Vast.ai worker: pick the ComfyUI or HartsyInference version from the list.\nIgnored when a Template is set (under Advanced).")]
        public string VastAIInstance_Image = CloudBackendTypes.DefaultVastImage;
        [SuggestionPlaceholder(Text = "pick a GPU, or leave blank for cheapest available")]
        [ConfigComment("The GPU to rent, with its VRAM and lowest current price.\nStart rents the cheapest machine with that GPU at that moment.\n'(cheapest available)' takes the cheapest single GPU with at least 16 GB of VRAM.")]
        public string VastAIInstance_GpuName = "";
        [ManualSettingsOptions(Vals = ["20", "30", "50", "75", "100", "150", "200"], ManualNames = ["20 GB", "30 GB", "50 GB", "75 GB", "100 GB", "150 GB", "200 GB"])]
        [ConfigComment("Size of the instance's own disk. It holds the worker image, so it must be well above the image size:\n30 GB or more for the ComfyUI image, 20 GB for HartsyInference. It's lost when the instance is destroyed.")]
        public int VastAIInstance_DiskGb = 30;
        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("The network volume with your models. It survives the instance being destroyed.\nCreate it on Vast.ai first; it then shows in this list.")]
        public string VastAIInstance_NetworkVolumeId = "";
        [ManualSettingsOptions(Vals = ["0", "30", "60", "120", "240", "480", "720", "1440"], ManualNames = ["Off", "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours", "12 hours", "24 hours"])]
        [ConfigComment("Safety stop: stops the instance after it has run this long, in case you forget it.")]
        public int VastAIInstance_MaxRuntimeMinutes = 240;
        [ManualSettingsOptions(Vals = ["0", "1", "2", "5", "10", "20", "50", "100"], ManualNames = ["Off", "$1", "$2", "$5", "$10", "$20", "$50", "$100"])]
        [ConfigComment("Safety stop: stops the instance once it has cost about this much (price per hour times time running).")]
        public double VastAIInstance_MaxSpendUsd = 10;
        // Advanced:
        [SuggestionPlaceholder(Text = "Vast.ai template hash")]
        [ConfigComment("Create the instance from one of your Vast.ai templates instead of the image above.\nThe template must run the Hartsy Vast.ai worker.")]
        public string VastAIInstance_TemplateHashId = "";
        [ConfigComment("Where the network volume is mounted inside the instance.\nThe Hartsy worker looks in /workspace; change it only for a custom image.")]
        public string VastAIInstance_VolumeMountPath = "/workspace";
        [ConfigComment("The port SwarmUI listens on inside the instance. The card opens it and passes it to the Hartsy worker.\nChange it only for a custom image that listens elsewhere.")]
        public int VastAIInstance_SwarmUIPort = 7801;
        [ConfigComment("Extra environment variables for new instances, one KEY=VALUE per line.")]
        public string VastAIInstance_Env = "";
        [ManualSettingsOptions(Vals = ["300", "600", "900", "1200", "1800"], ManualNames = ["5 minutes", "10 minutes", "15 minutes", "20 minutes", "30 minutes"])]
        [ConfigComment("How long to wait for the instance to boot and SwarmUI on it to answer.\nRaise it if the first start of a large image times out.")]
        public int VastAIInstance_StartupTimeoutSec = 900;
    }

    /// <summary>Settings the card folds into each section's collapsed Advanced group.</summary>
    public static readonly HashSet<string> AdvancedFields =
    [
        "RunPodServerless_StartupTimeoutSec",
        "RunPodPods_TemplateId", "RunPodPods_GpuCount", "RunPodPods_VolumeGb", "RunPodPods_VolumeMountPath", "RunPodPods_SwarmUIPort", "RunPodPods_PodEnv", "RunPodPods_StartupTimeoutSec",
        "VastAI_StartupTimeoutSec",
        "VastAIInstance_TemplateHashId", "VastAIInstance_VolumeMountPath", "VastAIInstance_SwarmUIPort", "VastAIInstance_Env", "VastAIInstance_StartupTimeoutSec"
    ];

    /// <summary>This backend's settings.</summary>
    Settings Config => (Settings)SettingsRaw;

    /// <summary>One provider section of the parent settings: its field prefix, display label, which API key it bills, whether it rents whole instances, its hidden backend type, and its setup guide in docs/setup.</summary>
    public record ProviderDef(string Prefix, string Label, string KeyType, bool IsInstance, Func<BackendHandler.BackendType> Type, string Guide);

    /// <summary>Where the setup guides are published, for the links on the card.</summary>
    public const string GuideBaseUrl = "https://github.com/HartsyAI/SwarmUI-CloudBackends/blob/main/docs/setup/";

    /// <summary>Every provider section this backend can manage, in settings-declaration order.</summary>
    public static readonly ProviderDef[] Providers =
    [
        new("RunPodServerless_", "RunPod Serverless", "runpod_api", IsInstance: false, () => CloudBackendTypes.RunPodServerless, "runpod-serverless.md"),
        new("RunPodPods_", "RunPod GPU Pods", "runpod_api", IsInstance: true, () => CloudBackendTypes.RunPodPods, "runpod-pods.md"),
        new("VastAI_", "Vast.ai Serverless", "vastai_api", IsInstance: false, () => CloudBackendTypes.VastAI, "vastai-serverless.md"),
        new("VastAIInstance_", "Vast.ai Instances", "vastai_api", IsInstance: true, () => CloudBackendTypes.VastAIInstance, "vastai-instances.md")
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
