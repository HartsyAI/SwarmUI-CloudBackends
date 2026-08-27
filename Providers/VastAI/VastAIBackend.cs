using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.DataHolders;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// SwarmUI backend for Vast.ai serverless endpoints.
/// Generation and worker lifecycle live in <see cref="CloudBackendBase"/>; this class supplies the
/// provider factory, API key lookup, and permission check.
/// </summary>
public class VastAIBackend : CloudBackendBase
{
    public class Settings : BaseSettings
    {
        // EndpointId holds the Vast.ai serverless endpoint NAME, which is what /route/ matches on.

        [SuggestionPlaceholder(Text = "handler")]
        [ConfigComment("Route on the worker that serves the SwarmUI wakeup handler.\nVast does not define a standard route name; this must match the route your worker image registers.")]
        public string WorkerRoute = "handler";
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    Settings VastConfig => (Settings)SettingsRaw;

    protected override ICloudProvider CreateProvider(string apiKey)
    {
        string route = string.IsNullOrWhiteSpace(VastConfig.WorkerRoute) ? "handler" : VastConfig.WorkerRoute.Trim();
        return new VastAIProvider(apiKey, BaseConfig.EndpointId?.Trim() ?? "", route);
    }

    protected override string GetApiKey(Session session)
    {
        if (session?.User is null)
        {
            throw new SwarmReadableErrorException("No user session. Log in and configure a Vast.ai API key in User Settings, API Keys.");
        }
        string key = session.User.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"Vast.ai API key not configured for user '{session.User.UserID}'. Set it in User Settings, API Keys, Vast.ai.");
        }
        return key;
    }

    public override void CheckPermission(Session session)
    {
        if (session?.User is null) { return; }
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseVastAI))
        {
            throw new SwarmReadableErrorException("You do not have permission to use Vast.ai backends.");
        }
    }

    /// <summary>The Vast endpoint is named, not an ID, but it still lives in the shared EndpointId setting.</summary>
    protected override void CheckRequiredConfig()
    {
        if (string.IsNullOrWhiteSpace(BaseConfig.EndpointId))
        {
            throw new SwarmReadableErrorException("No Vast.ai endpoint is set. Put your serverless endpoint's name in 'EndpointId'.");
        }
    }
}
