using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// SwarmUI backend for Vast.ai serverless endpoints.
/// Generation and worker lifecycle live in <see cref="CloudBackendBase"/>; this class supplies the
/// provider factory, API key lookup, and permission check.
/// </summary>
public class VastAIBackend : CloudBackendBase
{
    /// <summary>Vast.ai serverless settings. <see cref="BaseSettings.EndpointId"/> holds the endpoint NAME, which is what /route/ matches on.</summary>
    public class Settings : BaseSettings
    {
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    protected override ICloudProvider CreateProvider(string apiKey)
    {
        return new VastAIProvider(apiKey, BaseConfig.EndpointId?.Trim() ?? "");
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
