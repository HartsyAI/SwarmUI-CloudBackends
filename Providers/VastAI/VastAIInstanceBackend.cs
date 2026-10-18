using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// SwarmUI backend for Vast.ai rented instances - the Vast equivalent of <see cref="RunPod.RunPodPodsBackend"/>.
///
/// This backend only drives Vast's API to get an instance running with SwarmUI on it. Everything after
/// that is core Swarm: <see cref="CloudInstanceBackendBase"/> attaches a SwarmSwarmBackend to the
/// instance's URL, and that handles models, sessions and generation.
/// </summary>
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

        /// <summary>Extra KEY=VALUE environment variables, one per line.</summary>
        public string Env = "";

        /// <summary>SWARMUI_WORKER_TOKEN of an instance the user created, or blank to let this backend make one.</summary>
        [ValueIsSecret]
        public string WorkerToken = "";
    }

    public override InstanceSettings InstanceConfig => (Settings)SettingsRaw;

    Settings InstConfig => (Settings)SettingsRaw;

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
            Env = config.Env ?? "",
            WorkerToken = WorkerToken ?? "",
            DeleteOnStop = config.DeleteOnStop
        });
    }

    protected override string GetApiKey(User user)
    {
        string key = user?.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"Vast.ai API key not configured for user '{user?.UserID}'. Set it in User Settings, API Keys, Vast.ai.");
        }
        return key;
    }

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
    /// <remarks>Vast instances serve HTTPS with Vast's own CA, which the child cannot verify; it goes through a pinned relay.</remarks>
    protected override string GetChildAddress(string publicUrl)
    {
        if (!publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return publicUrl;
        }
        // A failed attach leaves the previous relay listening; close it before replacing it.
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
