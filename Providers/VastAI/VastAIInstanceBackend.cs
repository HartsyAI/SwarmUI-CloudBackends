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
    public class Settings : InstanceSettings
    {
        [SuggestionPlaceholder(Text = "leave blank to create one")]
        [ConfigComment("Existing Vast.ai instance ID to use.\nLeave blank to have Swarm find an instance by label, or create one.")]
        public string InstanceId = "";

        [ConfigComment("Port SwarmUI listens on inside the instance.\nDeclared to Vast as a Docker '-p' flag, so the instance's own SwarmUI must actually bind this port.")]
        public int SwarmUIPort = 7801;

        [ConfigComment("Destroy the instance when this backend shuts down, instead of just stopping it.\nStopping keeps the container disk (and keeps billing for that storage) so the instance can resume quickly.\nDestroying deletes it, which is only sensible when a network volume holds everything worth keeping.")]
        public bool TerminateOnShutdown = false;

        [SuggestionPlaceholder(Text = "blank = a label unique to this backend")]
        [ConfigComment("Label given to instances this backend creates, and used to find that instance again later.\nLeave blank to use a label unique to this backend (recommended) - a shared literal label across two or more Cloud Backends instances would make each backend's find-by-label step liable to attach to the other's instance instead of creating its own.")]
        public string Label = "";

        [SuggestionPlaceholder(Text = "docker image with SwarmUI")]
        [ConfigComment("Docker image to create the instance from. Ignored when TemplateHashId is set.")]
        public string Image = "";

        [SuggestionPlaceholder(Text = "Vast.ai template hash")]
        [ConfigComment("Vast.ai template to create the instance from, instead of naming an image directly.")]
        public string TemplateHashId = "";

        [SuggestionPlaceholder(Text = "pick an offer, or leave blank for cheapest available")]
        [ConfigComment("Specific rentable offer to create the instance from.\nLeave blank to search on-demand offers and take the cheapest match. Unlike RunPod's GPU-type retry, an offer is a specific host slot - if it's gone by the time Swarm tries to use it, pick another rather than expecting an automatic fallback.")]
        public string OfferId = "";

        [ConfigComment("Container disk size in GB for a created instance. This is wiped when the instance is destroyed.")]
        public int DiskGb = 20;

        [SuggestionPlaceholder(Text = "network volume id")]
        [ConfigComment("Existing network volume to attach, which is normally where SwarmUI and your models live.\nCreating a brand new named volume isn't supported here yet - attach one you already created on Vast.ai.")]
        public string NetworkVolumeId = "";

        [ConfigComment("Path the volume is mounted at inside the instance.")]
        public string VolumeMountPath = "/workspace";

        [ConfigComment("Extra environment variables for a created instance, as KEY=VALUE, one per line.\nThe SwarmUI port mapping is added automatically - no need to include it here.")]
        public string Env = "";
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
            AutoCreate = true,
            Label = string.IsNullOrWhiteSpace(config.Label) ? "swarmui-cloudbackends" : config.Label.Trim(),
            Image = config.Image?.Trim() ?? "",
            TemplateHashId = config.TemplateHashId?.Trim() ?? "",
            OfferId = config.OfferId?.Trim() ?? "",
            DiskGb = config.DiskGb,
            NetworkVolumeId = config.NetworkVolumeId?.Trim() ?? "",
            VolumeMountPath = config.VolumeMountPath?.Trim() ?? "/workspace",
            Env = config.Env ?? "",
            TerminateOnShutdown = config.TerminateOnShutdown
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
        if (session?.User is null) { return; }
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseVastAIInstances))
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
}
