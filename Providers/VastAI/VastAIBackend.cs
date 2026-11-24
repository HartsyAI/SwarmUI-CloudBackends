using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>Backend for Vast.ai serverless endpoints. Generation and worker lifecycle live in <see cref="CloudBackendBase"/>; this only supplies the provider, API key lookup and permission check.</summary>
public class VastAIBackend : CloudBackendBase
{
    /// <summary>Settings for this backend. <see cref="CloudBackendBase.BaseSettings.EndpointId"/> holds the endpoint's name, which is what /route/ matches on.</summary>
    public class Settings : BaseSettings
    {
    }

    /// <inheritdoc/>
    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    /// <inheritdoc/>
    protected override ICloudProvider CreateProvider(string apiKey)
    {
        return new VastAIProvider(apiKey, BaseConfig.EndpointId?.Trim() ?? "");
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
        if (!session.User.HasPermission(CloudBackends.PermUseVastAI))
        {
            throw new SwarmReadableErrorException("You do not have permission to use Vast.ai backends.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>A Vast endpoint is named rather than identified, but its name still lives in the shared EndpointId setting.</remarks>
    protected override void CheckRequiredConfig()
    {
        if (string.IsNullOrWhiteSpace(BaseConfig.EndpointId))
        {
            throw new SwarmReadableErrorException("No Vast.ai endpoint is set. Put your serverless endpoint's name in 'EndpointId'.");
        }
    }
}
