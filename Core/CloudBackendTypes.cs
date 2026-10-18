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
/// <see cref="CloudBackendsBackend"/> creates these as hidden (nonreal) children through
/// <see cref="BackendHandler.AddNewNonrealBackend"/>, so they never appear in "Add new backend" or the BackendType parameter.
/// </summary>
public static class CloudBackendTypes
{
    /// <summary>Default worker image for RunPod pods: the Hartsy RunPod worker, pinned to the release this version was tested with.</summary>
    public const string DefaultRunPodImage = "kalebbroo/swarmui-worker-runpod:2.0.0-comfyui";

    /// <summary>Default worker image for Vast.ai instances: the Hartsy Vast.ai worker, pinned to the release this version was tested with.</summary>
    public const string DefaultVastImage = "kalebbroo/swarmui-worker-vast:1.0.0-hartsyinference";

    /// <summary>The Hartsy worker images the card suggests for each instance section's image field.</summary>
    public static readonly Dictionary<string, (string Image, string Label)[]> WorkerImages = new()
    {
        ["RunPodPods_"] = [(DefaultRunPodImage, "Hartsy worker, ComfyUI"), ("kalebbroo/swarmui-worker-runpod:2.0.0-hartsyinference", "Hartsy worker, HartsyInference (needs a CUDA 13 GPU host)")],
        ["VastAIInstance_"] = [(DefaultVastImage, "Hartsy worker, HartsyInference (needs a CUDA 13 GPU host)"), ("kalebbroo/swarmui-worker-vast:1.0.0-comfyui", "Hartsy worker, ComfyUI")]
    };

    /// <summary>True for worker images whose backend needs a CUDA 13 driver: HartsyInference's GPU kernels are PTX 9.0.</summary>
    public static bool NeedsCuda13(string image)
    {
        return image?.Contains("hartsyinference", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>Hidden type record for the RunPod Serverless child backend.</summary>
    public static BackendHandler.BackendType RunPodServerless;

    /// <summary>Hidden type record for the RunPod GPU Pods child backend.</summary>
    public static BackendHandler.BackendType RunPodPods;

    /// <summary>Hidden type record for the Vast.ai Serverless child backend.</summary>
    public static BackendHandler.BackendType VastAI;

    /// <summary>Hidden type record for the Vast.ai Instances child backend.</summary>
    public static BackendHandler.BackendType VastAIInstance;

    /// <summary>Type record for the owner-gated swarm backend attached to a started cloud instance (see <see cref="OwnerBoundSwarmBackend"/>).</summary>
    public static BackendHandler.BackendType OwnerBoundSwarm;

    public static void Init()
    {
        RunPodServerless ??= BuildHidden<RunPodServerlessBackend>("runpod_serverless_hidden", "RunPod Serverless");
        RunPodPods ??= BuildHidden<RunPodPodsBackend>("runpod_pods_hidden", "RunPod GPU Pods");
        VastAI ??= BuildHidden<VastAIBackend>("vastai_serverless_hidden", "Vast.ai Serverless");
        VastAIInstance ??= BuildHidden<VastAIInstanceBackend>("vastai_instance_hidden", "Vast.ai Instances");
        OwnerBoundSwarm ??= BuildHidden<OwnerBoundSwarmBackend>("cloudbackends_owner_swarm_hidden", "Cloud Instance Swarm");
    }

    /// <summary>Builds a BackendType record for internal (nonreal-child) use only. Mirrors the relevant
    /// part of <see cref="BackendHandler.RegisterBackendType"/> without adding to the public registry.</summary>
    static BackendHandler.BackendType BuildHidden<T>(string id, string name) where T : AbstractBackend
    {
        // Walk base types too: a subclass of a core backend (OwnerBoundSwarmBackend) inherits its
        // settings class rather than re-nesting one, and GetNestedTypes never returns inherited types.
        Type settingsType = null;
        for (Type t = typeof(T); t is not null && settingsType is null; t = t.BaseType)
        {
            settingsType = t.GetNestedTypes().FirstOrDefault(n => n.IsSubclassOf(typeof(AutoConfiguration)));
        }
        AutoConfiguration.Internal.AutoConfigData settingsInternal = (Activator.CreateInstance(settingsType) as AutoConfiguration).InternalData.SharedData;
        return new(id, name, $"{name} (internal, managed by Cloud Backends)", settingsType, settingsInternal, typeof(T), NetDescription: null, CanLoadFast: true);
    }
}
