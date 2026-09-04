using System.Collections.Concurrent;
using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using Hartsy.Extensions.CloudBackends.WebAPI;
using Microsoft.AspNetCore.Html;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.CloudBackends;

/// <summary>
/// Entry point for the SwarmUI Cloud Backends extension.
/// Registers one user-facing backend type, "Cloud Backends" (<see cref="CloudBackendsBackend"/>), which
/// internally spins up hidden children for whichever of RunPod Serverless, RunPod GPU Pods, Vast.ai
/// Serverless, and Vast.ai Instances its settings have enabled. See <see cref="CloudBackendTypes"/> for
/// how those providers stay fully functional without being independently addable.
///
/// To add a future provider (e.g. Massed Compute):
///   1. Create Providers/MassedCompute/MassedComputeProvider.cs implementing ICloudProvider.
///   2. Create Providers/MassedCompute/MassedComputeBackend.cs extending CloudBackendBase.
///   3. Register it in CloudBackendTypes.Init(), add its enabled/settings fields to
///      CloudBackendsBackend.Settings, and add the branch in CloudBackendsBackend.Init().
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

    public static readonly PermInfo PermUseVastAIInstances = Permissions.Register(new PermInfo(
        "use_vastai_instances", "Use Vast.ai Instances",
        "Allows using Vast.ai on-demand rented instances for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    public static readonly PermInfo PermCloudStatus = Permissions.Register(new PermInfo(
        "cloudbackends_status", "Cloud Backends Status/Refresh",
        "Allows viewing status of, and refreshing models from, cloud GPU backends (each backend still requires its own provider permission).",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    // ── Extension lifecycle ───────────────────────────────────────────────────

    public override void OnPreInit()
    {
        Logs.Init("Initializing Hartsy's Cloud Backends Extension...");
        // Fills the RunPod Pods section with live GPU/volume/pod choices, and regroups the single
        // "Cloud Backends" settings card into one collapsible section per provider.
        ScriptFiles.Add("Assets/cloudbackends.js");
        StyleSheetFiles.Add("Assets/cloudbackends.css");
    }

    public override void OnInit()
    {
        // ── Backend types ─────────────────────────────────────────────────────
        // The three providers are no longer independently addable - CloudBackendTypes builds their
        // BackendType records for internal use only (nonreal children of CloudBackendsBackend below).
        // Their classes are otherwise unchanged and fully reused.
        CloudBackendTypes.Init();

        Program.Backends.RegisterBackendType<CloudBackendsBackend>(
            "cloud_backends", "Cloud Backends",
            "Rents GPU capacity from RunPod or Vast.ai. Enable one or more providers in the settings below.",
            CanLoadFast: true);

        // ── API keys ──────────────────────────────────────────────────────────
        RegisterApiKey("runpod_api", "RunPod", "https://www.runpod.io/console/user/settings",
            "Enter your RunPod API key. Get it from <a href='https://www.runpod.io/console/user/settings' target='_blank'>RunPod Settings</a>. Used for both Serverless and GPU Pod backends.");

        RegisterApiKey("vastai_api", "Vast.ai", "https://cloud.vast.ai/cli/",
            "Enter your Vast.ai API key. Get it from <a href='https://cloud.vast.ai/cli/' target='_blank'>Vast.ai Account Settings</a>.");

        // ── Remote model providers ────────────────────────────────────────────
        // Pods are deliberately absent here. A pod attaches core's own SwarmSwarmBackend, whose models
        // already reach the browser through the built-in "remote_swarm" provider, so registering our
        // own would list them twice.
        // Known limitation: ExtraModelProviders callbacks carry no session, so the model LIST is the
        // union across users' children; generation itself is still strictly per-user (a request routed
        // at another user's model gets a clean refusal). Per-user listing needs a core PR, not a hack.
        RegisterModelProvider<RunPodServerlessBackend>("runpod_serverless");
        RegisterModelProvider<VastAIBackend>("vastai_serverless");

        // ── Per-user child provisioning ───────────────────────────────────────
        // Backends are per-user (each runs on its owner's key), created on demand. This hook is the
        // only pre-backend-matching point that carries the requesting user, so it is where a user's
        // serverless children come into being. It MUST be registered before the routing hooks below,
        // so a child can exist by the time routing inspects children. Instance (pod) children spawn
        // only from the explicit start/status WebAPI routes - starting one bills, so a generation on
        // some unrelated backend must never trigger it.
        RegisterPerUserProvisioning();

        // ── PreGenerate auto-routing ──────────────────────────────────────────
        // Same reasoning: a pod's models belong to a real Swarm backend that core routes to normally.
        // The IDs here must match CloudBackendTypes' hidden BackendType records (T2IEngine matches
        // T2IParamTypes.BackendType against each live backend's own HandlerTypeData.ID directly - it
        // never looks the ID up in the public registry, so the hidden IDs work fine here).
        RegisterPreGenerateRouting<RunPodServerlessBackend>(CloudBackendTypes.RunPodServerless.ID);
        RegisterPreGenerateRouting<VastAIBackend>(CloudBackendTypes.VastAI.ID);

        // ── Web API ───────────────────────────────────────────────────────────
        CloudBackendsWebAPI.Register();

        Logs.Info("Cloud Backends extension loaded (one 'Cloud Backends' entry, providers: RunPod Serverless, RunPod GPU Pods, Vast.ai Serverless, Vast.ai Instances).");
    }

    // ── Registration helpers ──────────────────────────────────────────────────

    static void RegisterPerUserProvisioning()
    {
        T2IEngine.PreGenerateEvent += (p) =>
        {
            User user = p.UserInput.SourceSession?.User;
            if (user is null)
            {
                return;
            }
            CloudBackendsBackend[] parents = [.. Program.Backends.RunningBackendsOfType<CloudBackendsBackend>()];
            if (parents.Length is 0)
            {
                return;
            }
            string targetType = p.UserInput.Get(T2IParamTypes.BackendType, "Any") ?? "Any";
            foreach (CloudBackendsBackend.ProviderDef def in CloudBackendsBackend.Providers.Where(d => !d.IsInstance))
            {
                if (targetType.Equals(def.Type().ID, StringComparison.OrdinalIgnoreCase))
                {
                    // The request explicitly targets this cloud type, so the user's child must be ready
                    // BEFORE backend matching runs - a still-loading child is excluded from matching,
                    // which fails fast whenever any other backend is running. The wait is short: child
                    // Init is credential validation (capped 30s), never an instance boot. A readable
                    // throw here (no key on file, bad key) surfaces as the generation's error message.
                    Exception lastFail = null;
                    bool ready = false;
                    foreach (CloudBackendsBackend parent in parents.Where(par => par.IsProviderEnabled(def)))
                    {
                        try
                        {
                            AbstractT2IBackend child = parent.EnsureChildForUser(user, def, throwWhenUnavailable: true).GetAwaiter().GetResult();
                            CloudBackendsBackend.WaitForChildReady(child).GetAwaiter().GetResult();
                            ready = true;
                        }
                        catch (Exception ex) { lastFail = ex; }
                    }
                    if (!ready && lastFail is not null)
                    {
                        throw lastFail is SwarmReadableErrorException ? lastFail : new SwarmReadableErrorException(lastFail.Message);
                    }
                }
                else
                {
                    // Not targeted: warm the user's children in the background so their cloud models
                    // and routing become available, without delaying this (non-cloud) generation.
                    foreach (CloudBackendsBackend parent in parents)
                    {
                        _ = Utilities.RunCheckedTask(() => parent.EnsureChildForUser(user, def), "cloud backends child provisioning");
                    }
                }
            }
        };
    }

    static void RegisterApiKey(string keyType, string title, string createLink, string infoHtml)
    {
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add(keyType);
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey(keyType))
            {
                UserUpstreamApiKeys.Register(new UserUpstreamApiKeys.ApiKeyInfo(
                    KeyType: keyType,
                    JSPrefix: keyType.Replace("_api", "").Replace("_", ""),
                    Title: title,
                    CreateLink: createLink,
                    InfoHtml: new HtmlString(infoHtml)));
            }
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
                {
                    return;
                }
                string requestedModel = null;
                object m = p.UserInput.Get(T2IParamTypes.Model);
                if (m is T2IModel tm) { requestedModel = tm.Name; }
                else if (m is string ms) { requestedModel = ms; }
                if (string.IsNullOrWhiteSpace(requestedModel)) { return; }
                string bareName = requestedModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                    ? requestedModel[..^".safetensors".Length] : requestedModel;
                // Never divert a model the user already has locally onto a paid cloud GPU - the worker's
                // volume usually holds the same checkpoints, so this would silently bill for every gen.
                if (Program.MainSDModels.Models.ContainsKey(requestedModel) || Program.MainSDModels.Models.ContainsKey(bareName)) { return; }
                foreach (T b in Program.Backends.RunningBackendsOfType<T>())
                {
                    // Only the requesting user's own children may route their generation - another
                    // user's child would refuse it anyway (and bills a different account's key).
                    if (b.OwnerUserId != p.UserInput.SourceSession?.User?.UserID) { continue; }
                    ConcurrentDictionary<string, Dictionary<string, JObject>> rem = b.RemoteModels;
                    if (rem is null) { continue; }
                    string bare = bareName;
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
