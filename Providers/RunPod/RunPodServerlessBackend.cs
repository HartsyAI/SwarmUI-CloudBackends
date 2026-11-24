using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>Backend for RunPod Serverless endpoints. Generation and lifecycle live in <see cref="CloudBackendBase"/>; this only supplies the provider, API key lookup and permission check.</summary>
public class RunPodServerlessBackend : CloudBackendBase
{
    /// <summary>Settings for this backend, which adds none to <see cref="CloudBackendBase.BaseSettings"/>.</summary>
    public class Settings : BaseSettings
    {
    }

    /// <inheritdoc/>
    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    /// <inheritdoc/>
    protected override ICloudProvider CreateProvider(string apiKey) =>
        new RunPodServerlessProvider(apiKey, BaseConfig.EndpointId);

    /// <inheritdoc/>
    protected override string GetApiKey(User user)
    {
        string key = user?.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new SwarmReadableErrorException($"RunPod API key not configured for user '{user?.UserID}'. Set it in User Settings → API Keys → RunPod.");
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
        if (!session.User.HasPermission(CloudBackends.PermUseRunPodServerless))
        {
            throw new SwarmReadableErrorException("You do not have permission to use RunPod Serverless backends.");
        }
    }
}
