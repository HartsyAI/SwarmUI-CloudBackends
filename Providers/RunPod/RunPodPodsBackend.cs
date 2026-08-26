using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// SwarmUI backend for RunPod on-demand GPU pods.
/// All generation/lifecycle logic lives in <see cref="CloudBackendBase"/>;
/// this class supplies the provider factory, API key lookup, permission check,
/// and the pod-specific settings (pod ID, port, etc.).
/// </summary>
public class RunPodPodsBackend : CloudBackendBase
{
    public class Settings : BaseSettings
    {
        [ConfigComment("RunPod pod ID to resume (e.g. 'abc123xyz'). Find it in the RunPod dashboard.")]
        public string PodId = "";

        [ConfigComment("Port SwarmUI is listening on inside the pod (default: 7801). Used to build the proxy URL.")]
        public int SwarmUIPort = 7801;

        [ConfigComment("If true, attempt to create a new pod when PodId is not found. Requires ImageName and GpuTypeId.")]
        public bool AutoCreate = false;

        [ConfigComment("Docker image to use when AutoCreate=true (e.g. 'ghcr.io/lllyasviel/stable-diffusion-webui:latest').")]
        public string ImageName = "";

        [ConfigComment("GPU type ID for pod creation when AutoCreate=true (e.g. 'NVIDIA GeForce RTX 4090').")]
        public string GpuTypeId = "";

        [ConfigComment("Number of GPUs for pod creation when AutoCreate=true.")]
        public int GpuCount = 1;
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    private Settings PodConfig => (Settings)SettingsRaw;

    protected override ICloudProvider CreateProvider(string apiKey)
    {
        string podId = PodConfig.PodId;
        if (string.IsNullOrWhiteSpace(podId))
        {
            // Fall back to EndpointId for backward compatibility in case the user set it there.
            podId = BaseConfig.EndpointId;
        }
        if (string.IsNullOrWhiteSpace(podId))
            throw new SwarmReadableErrorException("RunPod Pod ID is not configured. Set 'Pod ID' in the backend settings.");
        return new RunPodPodsProvider(apiKey, podId, PodConfig.SwarmUIPort);
    }

    protected override string GetApiKey(Session session)
    {
        if (session?.User is null)
            throw new SwarmReadableErrorException("No user session — log in and configure a RunPod API key in User Settings → API Keys.");
        string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new SwarmReadableErrorException($"RunPod API key not configured for user '{session.User.UserID}'. Set it in User Settings → API Keys → RunPod.");
        return key;
    }

    public override void CheckPermission(Session session)
    {
        if (session?.User is null) return;
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseRunPodPods))
            throw new SwarmReadableErrorException("You do not have permission to use RunPod GPU Pod backends.");
    }
}
