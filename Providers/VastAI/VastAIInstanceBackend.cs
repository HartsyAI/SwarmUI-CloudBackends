using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>Backend for Vast.ai rented instances, the Vast equivalent of <see cref="RunPod.RunPodPodsBackend"/>. It only gets an instance running with SwarmUI on it; <see cref="CloudInstanceBackendBase"/> then attaches to its URL as a Swarm backend, which handles models, sessions and generation.</summary>
public class VastAIInstanceBackend : CloudInstanceBackendBase
{
    /// <summary>Settings copied in from the card's Vast.ai Instances section; the card's <see cref="CloudBackendsBackend.Settings"/> documents them.</summary>
    public class Settings : InstanceSettings
    {
        /// <summary>Existing instance to use, or blank to find or create one by <see cref="Label"/>.</summary>
        public string InstanceId = "";

        /// <summary>Port SwarmUI listens on inside the instance.</summary>
        public int SwarmUIPort = 7801;

        /// <summary>Stop destroys the instance instead of pausing it.</summary>
        public bool DeleteOnStop = true;

        /// <summary>Label for created instances, used to find the instance again. Always set by the card, unique per backend and user.</summary>
        public string Label = "";

        /// <summary>Docker image for created instances. Ignored when <see cref="TemplateHashId"/> is set.</summary>
        public string Image = "";

        /// <summary>Vast.ai template to create instances from instead of an image.</summary>
        public string TemplateHashId = "";

        /// <summary>GPU model to rent (Vast's gpu_name, e.g. "RTX 4090"), or blank for the cheapest suitable one.</summary>
        public string GpuName = "";

        /// <summary>Container disk size in GB.</summary>
        public int DiskGb = 30;

        /// <summary>Existing network volume to attach.</summary>
        public string NetworkVolumeId = "";

        /// <summary>Where the volume is mounted inside the instance.</summary>
        public string VolumeMountPath = "/workspace";

        /// <summary>Worker token of an instance the user created, or blank to let this backend make one.</summary>
        [ValueIsSecret]
        public string WorkerToken = "";
    }

    /// <inheritdoc/>
    public override InstanceSettings InstanceConfig => (Settings)SettingsRaw;

    /// <summary>This backend's settings, typed.</summary>
    Settings InstConfig => (Settings)SettingsRaw;

    /// <inheritdoc/>
    protected override ICloudInstanceProvider CreateProvider(string apiKey)
    {
        Settings config = InstConfig;
        return new VastAIInstanceProvider(apiKey, new VastAIInstancePlan
        {
            InstanceId = config.InstanceId?.Trim() ?? "",
            PersistedId = PersistedInstanceId ?? "",
            SwarmUIPort = config.SwarmUIPort,
            Label = string.IsNullOrWhiteSpace(config.Label) ? "swarmui-cloudbackends" : config.Label.Trim(),
            Image = config.Image?.Trim() ?? "",
            TemplateHashId = config.TemplateHashId?.Trim() ?? "",
            GpuName = config.GpuName?.Trim() ?? "",
            DiskGb = config.DiskGb,
            NetworkVolumeId = config.NetworkVolumeId?.Trim() ?? "",
            VolumeMountPath = config.VolumeMountPath?.Trim() ?? "/workspace",
            WorkerToken = WorkerToken ?? "",
            DeleteOnStop = config.DeleteOnStop
        });
    }

    /// <inheritdoc/>
    protected override string GetApiKey(User user)
    {
        string key = user?.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"Vast.ai API key not configured for user '{user?.UserID}'. Set it in User Settings, API Keys, Vast.ai.");
        }
        return key;
    }

    /// <inheritdoc/>
    public override void CheckPermission(Session session)
    {
        if (session?.User is null)
        {
            return;
        }
        if (!session.User.HasPermission(CloudBackends.PermUseVastAIInstances))
        {
            throw new SwarmReadableErrorException("You do not have permission to use Vast.ai Instance backends.");
        }
    }

    /// <inheritdoc/>
    protected override void CheckRequiredConfig()
    {
        Settings config = InstConfig;
        if (string.IsNullOrWhiteSpace(config.InstanceId) && string.IsNullOrWhiteSpace(config.Image) && string.IsNullOrWhiteSpace(config.TemplateHashId))
        {
            throw new SwarmReadableErrorException("Nothing to start. Set 'InstanceId' to use an existing instance, or set an 'Image' (or 'TemplateHashId') so one can be created.");
        }
    }

    /// <inheritdoc/>
    protected override string ExistingWorkerToken => InstConfig.WorkerToken;

    /// <summary>The TLS relay to the current instance, if any.</summary>
    VastTlsRelay Relay;

    /// <inheritdoc/>
    /// <remarks>Vast instances serve HTTPS with Vast's own CA, which the child cannot verify, so it goes through a pinned relay. A failed attach leaves the previous relay listening, so it is closed before being replaced.</remarks>
    protected override string GetChildAddress(string publicUrl)
    {
        if (!publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return publicUrl;
        }
        VastTlsRelay old = Relay;
        Relay = new VastTlsRelay(publicUrl);
        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
        return Relay.LocalUrl;
    }

    /// <inheritdoc/>
    protected override async Task OnChildDetachedAsync()
    {
        VastTlsRelay relay = Relay;
        Relay = null;
        if (relay is not null)
        {
            await relay.DisposeAsync();
        }
    }
}
