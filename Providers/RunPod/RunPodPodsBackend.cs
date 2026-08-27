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
///
/// This backend only drives RunPod's API to get a pod running with SwarmUI on it. Everything after
/// that is core Swarm: <see cref="CloudInstanceBackendBase"/> attaches a SwarmSwarmBackend to the
/// pod's URL, and that handles models, sessions and generation.
/// </summary>
public class RunPodPodsBackend : CloudInstanceBackendBase
{
    public class Settings : InstanceSettings
    {
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Existing RunPod pod ID to use.\nLeave blank to have Swarm find a pod by name, or create one.")]
        [ManualSettingsOptions(Vals = [""])]
        public string PodId = "";

        [ConfigComment("Port SwarmUI listens on inside the pod.\nThe pod must expose this as an http port so RunPod's proxy can reach it.")]
        public int SwarmUIPort = 7801;

        [ConfigComment("Destroy the pod when this backend shuts down, instead of just stopping it.\nStopping keeps the container disk (and keeps billing for that storage) so the pod can resume quickly.\nTerminating deletes it, which is only sensible when a network volume holds everything worth keeping.")]
        public bool TerminateOnShutdown = false;

        [SuggestionPlaceholder(Text = "swarmui-cloudbackends")]
        [ConfigComment("Name given to pods this backend creates, and used to find that pod again later so a restart reuses it rather than creating another.")]
        public string PodName = "swarmui-cloudbackends";

        [SuggestionPlaceholder(Text = "docker image with SwarmUI")]
        [ConfigComment("Docker image to create the pod from, for example 'kalebbroo/swarmui-runpod:latest'.\nIgnored when TemplateId is set.")]
        public string ImageName = "";

        [SuggestionPlaceholder(Text = "RunPod template id")]
        [ConfigComment("RunPod template to create the pod from, instead of naming an image directly.")]
        [ManualSettingsOptions(Vals = [""])]
        public string TemplateId = "";

        [SuggestionPlaceholder(Text = "pick a GPU, or leave blank for cheapest available")]
        [ConfigComment("GPU type for created pods.\nLeave blank to use whatever is available, cheapest first.\nRunPod places exactly one GPU type per request and does not fall back on its own, so naming a busy type simply fails.")]
        [ManualSettingsOptions(Vals = [""])]
        public string GpuTypeId = "";

        [ConfigComment("Number of GPUs attached to a created pod.")]
        public int GpuCount = 1;

        [ConfigComment("Container disk size in GB for a created pod. This is wiped when the pod is terminated.")]
        public int ContainerDiskGb = 50;

        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("Network volume to attach, which is normally where SwarmUI and your models live.\nThe pod is automatically placed in that volume's data center.")]
        [ManualSettingsOptions(Vals = [""])]
        public string NetworkVolumeId = "";

        [ConfigComment("Path the volume is mounted at inside the pod.")]
        public string VolumeMountPath = "/runpod-volume";

        [ConfigComment("Pod volume size in GB, used only when no network volume is attached. Zero for none.")]
        public int VolumeGb = 0;

        [ManualSettingsOptions(Vals = ["SECURE", "COMMUNITY"])]
        [ConfigComment("Which RunPod cloud to create pods in. Secure is more reliable, Community is cheaper.")]
        public string CloudType = "SECURE";

        [SuggestionPlaceholder(Text = "leave blank to let RunPod choose")]
        [ConfigComment("Restrict created pods to one data center.\nIgnored when a network volume is attached, since the volume fixes the data center.")]
        [ManualSettingsOptions(Vals = [""])]
        public string DataCenterId = "";

        [ConfigComment("Environment variables for a created pod, as KEY=VALUE, one per line.")]
        public string PodEnv = "";
    }

    public override InstanceSettings InstanceConfig => (Settings)SettingsRaw;

    Settings PodConfig => (Settings)SettingsRaw;

    /// <summary>Typed access to the pod provider, for the WebAPI routes.</summary>
    public RunPodPodsProvider PodsProvider => Provider as RunPodPodsProvider;

    protected override ICloudInstanceProvider CreateProvider(string apiKey)
    {
        Settings config = PodConfig;
        return new RunPodPodsProvider(apiKey, new RunPodPodPlan
        {
            PodId = config.PodId?.Trim() ?? "",
            SwarmUIPort = config.SwarmUIPort,
            AutoCreate = true,
            PodName = string.IsNullOrWhiteSpace(config.PodName) ? "swarmui-cloudbackends" : config.PodName.Trim(),
            ImageName = config.ImageName?.Trim() ?? "",
            TemplateId = config.TemplateId?.Trim() ?? "",
            GpuTypeId = config.GpuTypeId?.Trim() ?? "",
            GpuCount = config.GpuCount,
            ContainerDiskGb = config.ContainerDiskGb,
            VolumeGb = config.VolumeGb,
            VolumeMountPath = config.VolumeMountPath?.Trim() ?? "/runpod-volume",
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

    protected override void CheckRequiredConfig()
    {
        Settings config = PodConfig;
        if (string.IsNullOrWhiteSpace(config.PodId)
            && string.IsNullOrWhiteSpace(config.ImageName)
            && string.IsNullOrWhiteSpace(config.TemplateId))
        {
            throw new SwarmReadableErrorException("Nothing to start. Set 'PodId' to use an existing pod, or set an 'ImageName' (or 'TemplateId') so a pod can be created.");
        }
    }
}
