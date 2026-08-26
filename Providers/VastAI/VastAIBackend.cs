using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// SwarmUI backend for Vast.ai serverless endpoints.
/// All generation/lifecycle logic lives in <see cref="CloudBackendBase"/>;
/// this class only supplies the provider factory, API key lookup, and permission check.
/// </summary>
public class VastAIBackend : CloudBackendBase
{
    public class Settings : BaseSettings
    {
        // No extra settings needed beyond BaseSettings for Vast.ai.
        // EndpointId stores the Vast.ai endpoint name (e.g. "my-swarm-endpoint").
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    protected override ICloudProvider CreateProvider(string apiKey) =>
        new VastAIProvider(apiKey, BaseConfig.EndpointId);

    protected override string GetApiKey(Session session)
    {
        if (session?.User is null)
            throw new SwarmReadableErrorException("No user session — log in and configure a Vast.ai API key in User Settings → API Keys.");
        string key = session.User.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new SwarmReadableErrorException($"Vast.ai API key not configured for user '{session.User.UserID}'. Set it in User Settings → API Keys → Vast.ai.");
        return key;
    }

    public override void CheckPermission(Session session)
    {
        if (session?.User is null) return;
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseVastAI))
            throw new SwarmReadableErrorException("You do not have permission to use Vast.ai backends.");
    }
}
