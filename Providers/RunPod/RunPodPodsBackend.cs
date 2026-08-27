using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// SwarmUI backend for RunPod on-demand GPU pods.
/// Generation and worker lifecycle live in <see cref="CloudBackendBase"/>; this class supplies the
/// provider factory, API key lookup, permission check, and the pod-specific settings.
/// </summary>
public class RunPodPodsBackend : CloudBackendBase
{
    public class Settings : BaseSettings
    {
        [SuggestionPlaceholder(Text = "existing pod id")]
        [ConfigComment("RunPod pod ID to use, for example 'abc123xyz'. Find it in the RunPod console.\nLeave blank and enable AutoCreate to have Swarm find or create a pod for you.")]
        public string PodId = "";

        [ConfigComment("Port SwarmUI listens on inside the pod (default 7801).\nThe pod must expose this as an http port so RunPod's proxy can reach it.")]
        public int SwarmUIPort = 7801;

        [ConfigComment("Stop the pod when this backend is disabled or Swarm shuts down.\nStrongly recommended: a running pod bills continuously, even when idle.")]
        public bool StopPodOnShutdown = true;

        [ConfigComment("Destroy the pod on shutdown instead of just stopping it.\nThis deletes the container disk. Only useful with AutoCreate plus a network volume holding your models.")]
        public bool TerminateOnShutdown = false;

        [ConfigComment("Create a pod when no pod ID is set and no previously created pod is found.\nRequires either ImageName or TemplateId, plus a GPU type.")]
        public bool AutoCreate = false;

        [SuggestionPlaceholder(Text = "swarmui-cloudbackends")]
        [ConfigComment("Name given to auto-created pods, and used to find one again on restart so a new pod is not created every time.")]
        public string PodName = "swarmui-cloudbackends";

        [SuggestionPlaceholder(Text = "docker image with SwarmUI installed")]
        [ConfigComment("Docker image used when creating a pod, for example 'kalebbroo/swarmui-runpod:latest'.\nIgnored if TemplateId is set.")]
        public string ImageName = "";

        [SuggestionPlaceholder(Text = "RunPod template id")]
        [ConfigComment("RunPod template ID to create the pod from, instead of specifying an image directly.")]
        public string TemplateId = "";

        [SuggestionPlaceholder(Text = "NVIDIA RTX A4000")]
        [ConfigComment("GPU type for created pods, exactly as RunPod names it, for example 'NVIDIA RTX A4000' or 'NVIDIA GeForce RTX 4090'.")]
        public string GpuTypeId = "";

        [ConfigComment("Number of GPUs attached to a created pod.")]
        public int GpuCount = 1;

        [ConfigComment("Container disk size in GB for a created pod.")]
        public int ContainerDiskGb = 50;

        [ConfigComment("Pod volume size in GB for a created pod. Ignored when NetworkVolumeId is set.")]
        public int VolumeGb = 0;

        [ConfigComment("Path the volume is mounted at inside a created pod.")]
        public string VolumeMountPath = "/workspace";

        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("Network volume to attach to a created pod, which is where models and the SwarmUI install normally live.\nThe pod is placed in that volume's data center.")]
        public string NetworkVolumeId = "";

        [ManualSettingsOptions(Vals = ["SECURE", "COMMUNITY"])]
        [ConfigComment("Which RunPod cloud to create pods in. SECURE is more reliable, COMMUNITY is cheaper.")]
        public string CloudType = "SECURE";

        [SuggestionPlaceholder(Text = "eg US-KS-2")]
        [ConfigComment("Restrict created pods to one data center. Leave blank to let RunPod choose.\nIgnored when NetworkVolumeId is set, since the volume fixes the data center.")]
        public string DataCenterId = "";

        [ConfigComment("Environment variables for a created pod, as KEY=VALUE, one per line.")]
        public string PodEnv = "";
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    Settings PodConfig => (Settings)SettingsRaw;

    /// <summary>Typed access to the pod provider, for the WebAPI stop/terminate routes.</summary>
    public RunPodPodsProvider PodsProvider => Provider as RunPodPodsProvider;

    protected override ICloudProvider CreateProvider(string apiKey)
    {
        Settings config = PodConfig;
        // EndpointId is the shared base setting; accept it as a pod ID for backends configured before
        // PodId existed, so those keep working instead of silently failing validation.
        string podId = string.IsNullOrWhiteSpace(config.PodId) ? BaseConfig.EndpointId : config.PodId;
        return new RunPodPodsProvider(apiKey, new RunPodPodPlan
        {
            PodId = podId?.Trim() ?? "",
            SwarmUIPort = config.SwarmUIPort,
            AutoCreate = config.AutoCreate,
            PodName = string.IsNullOrWhiteSpace(config.PodName) ? "swarmui-cloudbackends" : config.PodName.Trim(),
            ImageName = config.ImageName?.Trim() ?? "",
            TemplateId = config.TemplateId?.Trim() ?? "",
            GpuTypeId = config.GpuTypeId?.Trim() ?? "",
            GpuCount = config.GpuCount,
            ContainerDiskGb = config.ContainerDiskGb,
            VolumeGb = config.VolumeGb,
            VolumeMountPath = config.VolumeMountPath?.Trim() ?? "/workspace",
            NetworkVolumeId = config.NetworkVolumeId?.Trim() ?? "",
            CloudType = config.CloudType,
            DataCenterId = config.DataCenterId?.Trim() ?? "",
            Env = config.PodEnv ?? "",
            TerminateOnShutdown = config.TerminateOnShutdown
        });
    }

    protected override string GetApiKey(Session session)
    {
        if (session?.User is null)
        {
            throw new SwarmReadableErrorException("No user session. Log in and configure a RunPod API key in User Settings, API Keys.");
        }
        string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"RunPod API key not configured for user '{session.User.UserID}'. Set it in User Settings, API Keys, RunPod.");
        }
        return key;
    }

    public override void CheckPermission(Session session)
    {
        if (session?.User is null) { return; }
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseRunPodPods))
        {
            throw new SwarmReadableErrorException("You do not have permission to use RunPod GPU Pod backends.");
        }
    }

    /// <summary>
    /// Releases the pod before the shared shutdown runs. A pod bills for every minute it stays up, so
    /// leaving one running after the backend is disabled is a silent, open-ended charge.
    /// </summary>
    public override async Task Shutdown()
    {
        RunPodPodsProvider pods = PodsProvider;
        if (pods is not null && PodConfig.StopPodOnShutdown)
        {
            try { await pods.ReleasePodAsync(); }
            catch (Exception ex) { Logs.Error($"[RunPodPods] Failed to release pod on shutdown, it may still be billing: {ex.ReadableString()}"); }
        }
        else if (pods is not null && !string.IsNullOrWhiteSpace(pods.ActivePodId))
        {
            Logs.Warning($"[RunPodPods] Leaving pod '{pods.ActivePodId}' running because StopPodOnShutdown is off. It continues to bill until stopped.");
        }
        await base.Shutdown();
    }
}
