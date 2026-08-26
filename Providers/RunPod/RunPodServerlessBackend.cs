using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// SwarmUI backend for RunPod Serverless endpoints.
/// All generation/lifecycle logic lives in <see cref="CloudBackendBase"/>;
/// this class only supplies the provider factory, API key lookup, and permission check.
/// </summary>
public class RunPodServerlessBackend : CloudBackendBase
{
    public class Settings : BaseSettings
    {
    }

    public override BaseSettings BaseConfig => (Settings)SettingsRaw;

    protected override ICloudProvider CreateProvider(string apiKey) =>
        new RunPodServerlessProvider(apiKey, BaseConfig.EndpointId);

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
        if (!session.User.HasPermission(CloudBackendsExtension.PermUseRunPodServerless))
            throw new SwarmReadableErrorException("You do not have permission to use RunPod Serverless backends.");
    }
}
