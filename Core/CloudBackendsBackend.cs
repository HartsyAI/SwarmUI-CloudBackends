using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using SwarmUI.Backends;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// The single user-facing "Cloud Backends" entry. Never generates anything itself - on Init it spins up
/// a hidden (nonreal) child backend for each provider section the user has enabled, reusing the existing
/// <see cref="RunPodServerlessBackend"/>/<see cref="RunPodPodsBackend"/>/<see cref="VastAIBackend"/>
/// classes unchanged. On Shutdown (including a settings-edit re-Init) it tears all of them down again, so
/// the child set always matches the current toggle state.
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
        [ConfigComment("Refresh available models from the worker on backend init (background).")]
        public bool RunPodServerless_AutoRefresh = false;

        // ── RunPod GPU Pods ──────────────────────────────────────────────────
        [ConfigComment("Enable a RunPod GPU Pod.")]
        public bool RunPodPods_Enabled = false;
        [ConfigComment("Existing RunPod pod ID to use.\nLeave blank to have Swarm find a pod by name, or create one.")]
        public string RunPodPods_PodId = "";
        [ConfigComment("Port SwarmUI listens on inside the pod.")]
        public int RunPodPods_SwarmUIPort = 7801;
        [ConfigComment("Destroy the pod when this backend shuts down, instead of just stopping it.")]
        public bool RunPodPods_TerminateOnShutdown = false;
        [ConfigComment("Name given to pods this backend creates, and used to find that pod again later.")]
        public string RunPodPods_PodName = "swarmui-cloudbackends";
        [ConfigComment("Docker image to create the pod from. Ignored when TemplateId is set.")]
        public string RunPodPods_ImageName = "";
        [ConfigComment("RunPod template to create the pod from, instead of naming an image directly.")]
        public string RunPodPods_TemplateId = "";
        [ConfigComment("GPU type for created pods. Leave blank to use whatever is available, cheapest first.")]
        public string RunPodPods_GpuTypeId = "";
        [ConfigComment("Number of GPUs attached to a created pod.")]
        public int RunPodPods_GpuCount = 1;
        [ConfigComment("Container disk size in GB for a created pod.")]
        public int RunPodPods_ContainerDiskGb = 50;
        [ConfigComment("Network volume to attach, which is normally where SwarmUI and your models live.")]
        public string RunPodPods_NetworkVolumeId = "";
        [ConfigComment("Path the volume is mounted at inside the pod.")]
        public string RunPodPods_VolumeMountPath = "/runpod-volume";
        [ConfigComment("Pod volume size in GB, used only when no network volume is attached. Zero for none.")]
        public int RunPodPods_VolumeGb = 0;
        [ConfigComment("Which RunPod cloud to create pods in. Secure is more reliable, Community is cheaper.")]
        public string RunPodPods_CloudType = "SECURE";
        [ConfigComment("Restrict created pods to one data center. Ignored when a network volume is attached.")]
        public string RunPodPods_DataCenterId = "";
        [ConfigComment("Environment variables for a created pod, as KEY=VALUE, one per line.")]
        public string RunPodPods_PodEnv = "";
        [ConfigComment("How long to wait for the pod to boot and for SwarmUI on it to answer, in seconds.")]
        public int RunPodPods_StartupTimeoutSec = 900;
        [ConfigComment("How often to poll RunPod while waiting for the pod to start, in milliseconds.")]
        public int RunPodPods_PollIntervalMs = 5000;
        [ConfigComment("Start (creating it if needed) the pod as soon as this section is enabled and saved.\nOff by default: with the Start Pod button available, enabling this section should not silently create/bill a pod - use the button, or turn this on if you want it automatic.")]
        public bool RunPodPods_StartOnEnable = false;
        [ConfigComment("Failsafe: auto-stop the pod after it has been running this many minutes, in case you forgot to turn it off. Zero disables this check.")]
        public int RunPodPods_MaxRuntimeMinutes = 0;
        [ConfigComment("Failsafe: auto-stop the pod once its estimated spend (hourly rate x time running) reaches this many US dollars. Zero disables this check.")]
        public double RunPodPods_MaxSpendUsd = 0;

        // ── Vast.ai Serverless ───────────────────────────────────────────────
        [ConfigComment("Enable a Vast.ai Serverless worker.")]
        public bool VastAI_Enabled = false;
        [ConfigComment("Vast.ai serverless endpoint NAME (matches on /route/).")]
        public string VastAI_EndpointId = "";
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
        [ConfigComment("Refresh available models from the worker on backend init (background).")]
        public bool VastAI_AutoRefresh = false;
    }

    Settings Config => (Settings)SettingsRaw;

    /// <summary>IDs of the nonreal children this instance currently owns, so re-Init/Shutdown can clean them up.</summary>
    readonly List<int> ChildIds = [];

    /// <summary>Control backend only - never loads models or generates directly.</summary>
    public override IEnumerable<string> SupportedFeatures => [];

    public override async Task Init()
    {
        CanLoadModels = false;
        MaxUsages = 1;
        // Settings edits re-run Init without a guaranteed prior Shutdown - always start from a clean slate.
        await TearDownChildren();
        Settings config = Config;
        int enabledCount = 0;
        if (config.RunPodServerless_Enabled)
        {
            RunPodServerlessBackend.Settings settings = new()
            {
                EndpointId = config.RunPodServerless_EndpointId,
                MaxConcurrent = config.RunPodServerless_MaxConcurrent,
                PollIntervalMs = config.RunPodServerless_PollIntervalMs,
                StartupTimeoutSec = config.RunPodServerless_StartupTimeoutSec,
                GenerationTimeoutSec = config.RunPodServerless_GenerationTimeoutSec,
                KeepaliveSeconds = config.RunPodServerless_KeepaliveSeconds,
                AutoRefresh = config.RunPodServerless_AutoRefresh
            };
            AddChild(CloudBackendTypes.RunPodServerless, settings, "RunPod Serverless");
            enabledCount++;
        }
        if (config.RunPodPods_Enabled)
        {
            RunPodPodsBackend.Settings settings = new()
            {
                PodId = config.RunPodPods_PodId,
                SwarmUIPort = config.RunPodPods_SwarmUIPort,
                TerminateOnShutdown = config.RunPodPods_TerminateOnShutdown,
                PodName = config.RunPodPods_PodName,
                ImageName = config.RunPodPods_ImageName,
                TemplateId = config.RunPodPods_TemplateId,
                GpuTypeId = config.RunPodPods_GpuTypeId,
                GpuCount = config.RunPodPods_GpuCount,
                ContainerDiskGb = config.RunPodPods_ContainerDiskGb,
                NetworkVolumeId = config.RunPodPods_NetworkVolumeId,
                VolumeMountPath = config.RunPodPods_VolumeMountPath,
                VolumeGb = config.RunPodPods_VolumeGb,
                CloudType = config.RunPodPods_CloudType,
                DataCenterId = config.RunPodPods_DataCenterId,
                PodEnv = config.RunPodPods_PodEnv,
                StartupTimeoutSec = config.RunPodPods_StartupTimeoutSec,
                PollIntervalMs = config.RunPodPods_PollIntervalMs,
                StartOnEnable = config.RunPodPods_StartOnEnable,
                MaxRuntimeMinutes = config.RunPodPods_MaxRuntimeMinutes,
                MaxSpendUsd = config.RunPodPods_MaxSpendUsd
            };
            AddChild(CloudBackendTypes.RunPodPods, settings, "RunPod GPU Pods");
            enabledCount++;
        }
        if (config.VastAI_Enabled)
        {
            VastAIBackend.Settings settings = new()
            {
                EndpointId = config.VastAI_EndpointId,
                WorkerRoute = config.VastAI_WorkerRoute,
                MaxConcurrent = config.VastAI_MaxConcurrent,
                PollIntervalMs = config.VastAI_PollIntervalMs,
                StartupTimeoutSec = config.VastAI_StartupTimeoutSec,
                GenerationTimeoutSec = config.VastAI_GenerationTimeoutSec,
                KeepaliveSeconds = config.VastAI_KeepaliveSeconds,
                AutoRefresh = config.VastAI_AutoRefresh
            };
            AddChild(CloudBackendTypes.VastAI, settings, "Vast.ai Serverless");
            enabledCount++;
        }
        Status = BackendStatus.RUNNING;
        AddLoadStatus(enabledCount is 0
            ? "No provider enabled - nothing started. Expand a provider section, enable it, and Save."
            : $"{enabledCount} provider(s) enabled and starting.");
    }

    void AddChild(BackendHandler.BackendType type, AutoConfiguration settings, string label)
    {
        BackendHandler.BackendData data = Handler.AddNewNonrealBackend(type, BackendData, settings, newData =>
        {
            newData.AbstractBackend.Title = $"[Cloud Backends #{BackendData.ID}] {label}";
            // AddNewNonrealBackend takes a `parent` argument but (as of this writing) never assigns it -
            // set it ourselves so WebAPI calls can find "the RunPod Pods child of backend #N" by the one
            // ID the frontend actually has (the parent's), instead of needing the child's own hidden
            // negative ID, which is never exposed to the UI.
            newData.AbstractParent = BackendData;
        });
        ChildIds.Add(data.ID);
    }

    async Task TearDownChildren()
    {
        foreach (int id in ChildIds)
        {
            try { await Handler.DeleteById(id); }
            catch (Exception ex) { Logs.Debug($"[CloudBackends] Removing child backend #{id} failed: {ex.Message}"); }
        }
        ChildIds.Clear();
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
