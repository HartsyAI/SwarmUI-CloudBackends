using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using SwarmUI.Backends;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Builds <see cref="BackendHandler.BackendType"/> records for the individual provider backends
/// (RunPod Serverless, RunPod GPU Pods, Vast.ai Serverless, Vast.ai Instances) WITHOUT calling
/// <see cref="BackendHandler.RegisterBackendType"/>.
///
/// The provider classes are otherwise unchanged and fully functional - they just aren't independently
/// addable any more. <see cref="CloudBackendsBackend"/> is the one user-facing "Add new backend" entry;
/// it spins these up as hidden (nonreal) children via <see cref="BackendHandler.AddNewNonrealBackend"/>,
/// which only needs a BackendType record (BackendClass + SettingsClass) - it never touches the public
/// registry that feeds the "Add new backend" button list or the per-generation BackendType dropdown.
/// </summary>
public static class CloudBackendTypes
{
    public static BackendHandler.BackendType RunPodServerless { get; private set; }
    public static BackendHandler.BackendType RunPodPods { get; private set; }
    public static BackendHandler.BackendType VastAI { get; private set; }
    public static BackendHandler.BackendType VastAIInstance { get; private set; }

    public static void Init()
    {
        RunPodServerless ??= BuildHidden<RunPodServerlessBackend>("runpod_serverless_hidden", "RunPod Serverless");
        RunPodPods ??= BuildHidden<RunPodPodsBackend>("runpod_pods_hidden", "RunPod GPU Pods");
        VastAI ??= BuildHidden<VastAIBackend>("vastai_serverless_hidden", "Vast.ai Serverless");
        VastAIInstance ??= BuildHidden<VastAIInstanceBackend>("vastai_instance_hidden", "Vast.ai Instances");
    }

    /// <summary>Builds a BackendType record for internal (nonreal-child) use only. Mirrors the relevant
    /// part of <see cref="BackendHandler.RegisterBackendType"/> without adding to the public registry.</summary>
    static BackendHandler.BackendType BuildHidden<T>(string id, string name) where T : AbstractBackend
    {
        Type settingsType = typeof(T).GetNestedTypes().First(t => t.IsSubclassOf(typeof(AutoConfiguration)));
        AutoConfiguration.Internal.AutoConfigData settingsInternal = (Activator.CreateInstance(settingsType) as AutoConfiguration).InternalData.SharedData;
        return new(id, name, $"{name} (internal, managed by Cloud Backends)", settingsType, settingsInternal, typeof(T), NetDescription: null, CanLoadFast: true);
    }
}
