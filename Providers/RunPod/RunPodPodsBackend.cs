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
    /// <summary>Settings copied in from the card's RunPod GPU Pods section; the card's <see cref="CloudBackendsBackend.Settings"/> documents them.</summary>
    public class Settings : InstanceSettings
    {
        /// <summary>Existing pod to use, or blank to find or create one by <see cref="PodName"/>.</summary>
        public string PodId = "";

        /// <summary>Port SwarmUI listens on inside the pod.</summary>
        public int SwarmUIPort = 7801;

        /// <summary>Stop terminates the pod instead of pausing it.</summary>
        public bool DeleteOnStop = true;

        /// <summary>Name for created pods, used to find the pod again. Always set by the card, unique per backend and user.</summary>
        public string PodName = "";

        /// <summary>Docker image for created pods. Ignored when <see cref="TemplateId"/> is set.</summary>
        public string ImageName = "";

        /// <summary>RunPod template to create pods from instead of an image.</summary>
        public string TemplateId = "";

        /// <summary>GPU type for created pods, or blank for the cheapest available.</summary>
        public string GpuTypeId = "";

        /// <summary>GPUs per created pod.</summary>
        public int GpuCount = 1;

        /// <summary>Container disk size in GB.</summary>
        public int ContainerDiskGb = 50;

        /// <summary>Network volume to attach.</summary>
        public string NetworkVolumeId = "";

        /// <summary>Where the volume is mounted inside the pod.</summary>
        public string VolumeMountPath = "/runpod-volume";

        /// <summary>Pod volume size in GB, used only without a network volume.</summary>
        public int VolumeGb = 0;

        /// <summary>SECURE or COMMUNITY.</summary>
        public string CloudType = "SECURE";

        /// <summary>Data center for created pods, used only without a network volume.</summary>
        public string DataCenterId = "";

        /// <summary>Extra KEY=VALUE environment variables, one per line.</summary>
        public string PodEnv = "";

        /// <summary>SWARMUI_WORKER_TOKEN of a pod the user created, or blank to let this backend make one.</summary>
        [ValueIsSecret]
        public string WorkerToken = "";
    }

    public override InstanceSettings InstanceConfig => (Settings)SettingsRaw;

    Settings PodConfig => (Settings)SettingsRaw;

    protected override ICloudInstanceProvider CreateProvider(string apiKey)
    {
        Settings config = PodConfig;
        return new RunPodPodsProvider(apiKey, new RunPodPodPlan
        {
            PodId = config.PodId?.Trim() ?? "",
            PersistedId = PersistedInstanceId ?? "",
            SwarmUIPort = config.SwarmUIPort,
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
            WorkerToken = WorkerToken ?? "",
            DeleteOnStop = config.DeleteOnStop
        });
    }

    protected override string GetApiKey(User user)
    {
        string key = user?.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"RunPod API key not configured for user '{user?.UserID}'. Set it in User Settings, API Keys, RunPod.");
        }
        return key;
    }

    public override void CheckPermission(Session session)
    {
        if (session?.User is null)
        {
            return;
        }
        if (!session.User.HasPermission(CloudBackends.PermUseRunPodPods))
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

    /// <inheritdoc/>
    protected override string ExistingWorkerToken => PodConfig.WorkerToken;
}
