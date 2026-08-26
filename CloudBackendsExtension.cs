using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using Hartsy.Extensions.CloudBackends.WebAPI;
using Microsoft.AspNetCore.Html;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.CloudBackends;

/// <summary>
/// Entry point for the SwarmUI Cloud Backends extension.
/// Registers three backend types: RunPod Serverless, RunPod GPU Pods, and Vast.ai.
///
/// To add a future provider (e.g. Massed Compute):
///   1. Create Providers/MassedCompute/MassedComputeProvider.cs implementing ICloudProvider.
///   2. Create Providers/MassedCompute/MassedComputeBackend.cs extending CloudBackendBase.
///   3. Add the four registration lines in OnInit() below.
/// </summary>
public class CloudBackendsExtension : Extension
{
    // ── Permissions ───────────────────────────────────────────────────────────
    // One permission per provider so admins can grant access selectively.

    public static readonly PermInfoGroup CloudPermGroup = new("CloudBackends", "Permissions related to cloud GPU backends.");

    public static readonly PermInfo PermUseRunPodServerless = Permissions.Register(new PermInfo(
        "use_runpod_serverless", "Use RunPod Serverless",
        "Allows using RunPod's serverless GPU endpoints for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    public static readonly PermInfo PermUseRunPodPods = Permissions.Register(new PermInfo(
        "use_runpod_pods", "Use RunPod GPU Pods",
        "Allows using RunPod on-demand GPU pods for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    public static readonly PermInfo PermUseVastAI = Permissions.Register(new PermInfo(
        "use_vastai", "Use Vast.ai",
        "Allows using Vast.ai's serverless GPU endpoints for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    public static readonly PermInfo PermCloudStatus = Permissions.Register(new PermInfo(
        "cloudbackends_status", "Cloud Backends Status/Refresh",
        "Allows viewing status of, and refreshing models from, cloud GPU backends (each backend still requires its own provider permission).",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    // ── Extension lifecycle ───────────────────────────────────────────────────

    public override void OnPreInit()
    {
        Logs.Init("Initializing Hartsy's Cloud Backends Extension...");
    }

    public override void OnInit()
    {
        // ── Backend types ─────────────────────────────────────────────────────
        Program.Backends.RegisterBackendType<RunPodServerlessBackend>(
            "runpod_serverless", "RunPod Serverless",
            "Serverless GPU inference via RunPod with direct SwarmUI API access. Requires a custom serverless handler.",
            CanLoadFast: true);

        Program.Backends.RegisterBackendType<RunPodPodsBackend>(
            "runpod_pods", "RunPod GPU Pods",
            "On-demand GPU pod via RunPod. Resume a stopped pod and connect to SwarmUI running inside it.",
            CanLoadFast: true);

        Program.Backends.RegisterBackendType<VastAIBackend>(
            "vastai_serverless", "Vast.ai Serverless",
            "Serverless GPU inference via Vast.ai with direct SwarmUI API access. Supports on-demand scaling.",
            CanLoadFast: true);

        // ── API keys ──────────────────────────────────────────────────────────
        RegisterApiKey("runpod_api", "RunPod", "https://www.runpod.io/console/user/settings",
            "Enter your RunPod API key. Get it from <a href='https://www.runpod.io/console/user/settings' target='_blank'>RunPod Settings</a>. Used for both Serverless and GPU Pod backends.");

        RegisterApiKey("vastai_api", "Vast.ai", "https://cloud.vast.ai/cli/",
            "Enter your Vast.ai API key. Get it from <a href='https://cloud.vast.ai/cli/' target='_blank'>Vast.ai Account Settings</a>.");

        // ── Remote model providers ────────────────────────────────────────────
        RegisterModelProvider<RunPodServerlessBackend>("runpod_serverless");
        RegisterModelProvider<RunPodPodsBackend>("runpod_pods");
        RegisterModelProvider<VastAIBackend>("vastai_serverless");

        // ── PreGenerate auto-routing ──────────────────────────────────────────
        RegisterPreGenerateRouting<RunPodServerlessBackend>("runpod_serverless");
        RegisterPreGenerateRouting<RunPodPodsBackend>("runpod_pods");
        RegisterPreGenerateRouting<VastAIBackend>("vastai_serverless");

        // ── Web API ───────────────────────────────────────────────────────────
        CloudBackendsWebAPI.Register();

        Logs.Info("Cloud Backends extension loaded (RunPod Serverless, RunPod GPU Pods, Vast.ai).");
    }

    // ── Registration helpers ──────────────────────────────────────────────────

    static void RegisterApiKey(string keyType, string title, string createLink, string infoHtml)
    {
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add(keyType);
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey(keyType))
                UserUpstreamApiKeys.Register(new UserUpstreamApiKeys.ApiKeyInfo(
                    KeyType: keyType,
                    JSPrefix: keyType.Replace("_api", "").Replace("_", ""),
                    Title: title,
                    CreateLink: createLink,
                    InfoHtml: new HtmlString(infoHtml)));
        }
        catch (Exception ex) { Logs.Error($"[CloudBackends] Failed to register API key '{keyType}': {ex.Message}"); }
    }

    static void RegisterModelProvider<T>(string backendTypeId) where T : CloudBackendBase
    {
        try
        {
            if (!ModelsAPI.ExtraModelProviders.ContainsKey(backendTypeId))
            {
                ModelsAPI.ExtraModelProviders[backendTypeId] = (string subtype) =>
                {
                    T[] backs = [.. Program.Backends.RunningBackendsOfType<T>().Where(b => b.RemoteModels is not null)];
                    IEnumerable<Dictionary<string, JObject>> sets = backs
                        .Select(b => b.RemoteModels.GetValueOrDefault(subtype))
                        .Where(s => s is not null);
                    return sets.Any() ? sets.Aggregate((a, b) => a.Union(b).PairsToDictionary(false)) : [];
                };
            }
        }
        catch (Exception ex) { Logs.Error($"[CloudBackends] Failed to register model provider for '{backendTypeId}': {ex.Message}"); }
    }

    static void RegisterPreGenerateRouting<T>(string backendTypeId) where T : CloudBackendBase
    {
        try
        {
            T2IEngine.PreGenerateEvent += (p) =>
            {
                string currentType = p.UserInput.Get(T2IParamTypes.BackendType, "Any");
                if (!string.IsNullOrEmpty(currentType) && !currentType.Equals("Any", StringComparison.OrdinalIgnoreCase))
                    return;
                string requestedModel = null;
                object m = p.UserInput.Get(T2IParamTypes.Model);
                if (m is T2IModel tm) { requestedModel = tm.Name; }
                else if (m is string ms) { requestedModel = ms; }
                if (string.IsNullOrWhiteSpace(requestedModel)) return;
                foreach (T b in Program.Backends.RunningBackendsOfType<T>())
                {
                    var rem = b.RemoteModels;
                    if (rem is null) continue;
                    string bare = requestedModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                        ? requestedModel[..^".safetensors".Length] : requestedModel;
                    bool found = rem.Values.Any(dict =>
                        dict.ContainsKey(requestedModel) || dict.ContainsKey(bare)
                        || dict.Keys.Any(k => k.Equals(requestedModel.AfterLast('/'), StringComparison.OrdinalIgnoreCase))
                        || dict.Keys.Any(k => k.Equals(bare.AfterLast('/'), StringComparison.OrdinalIgnoreCase)));
                    if (found)
                    {
                        Logs.Verbose($"[CloudBackends] Auto-routing '{requestedModel}' to backend type '{backendTypeId}'");
                        p.UserInput.Set(T2IParamTypes.BackendType, backendTypeId);
                        return;
                    }
                }
            };
        }
        catch (Exception ex) { Logs.Error($"[CloudBackends] Failed to register PreGenerateEvent for '{backendTypeId}': {ex.Message}"); }
    }
}
