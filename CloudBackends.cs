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
/// Registers one user-facing backend type, "Cloud Backends" (<see cref="CloudBackendsBackend"/>), which creates hidden
/// per-user children for whichever of RunPod Serverless, RunPod GPU Pods, Vast.ai Serverless and Vast.ai Instances its
/// settings enable.
///
/// To add a provider: implement <see cref="ICloudProvider"/> (or <see cref="ICloudInstanceProvider"/>) and a backend on
/// <see cref="CloudBackendBase"/> (or <see cref="CloudInstanceBackendBase"/>), register its type in
/// <see cref="CloudBackendTypes"/>, then add its prefixed fields to <see cref="CloudBackendsBackend.Settings"/> and its
/// entries to <see cref="CloudBackendsBackend.Providers"/> and <see cref="CloudBackendsBackend.IsProviderEnabled"/>.
/// </summary>
public class CloudBackends : Extension
{
    // ── Permissions ───────────────────────────────────────────────────────────

    /// <summary>Permission group for everything this extension adds.</summary>
    public static readonly PermInfoGroup CloudPermGroup = new("CloudBackends", "Permissions related to cloud GPU backends.");

    /// <summary>Generating on RunPod Serverless workers.</summary>
    public static readonly PermInfo PermUseRunPodServerless = Permissions.Register(new PermInfo(
        "use_runpod_serverless", "Use RunPod Serverless",
        "Allows using RunPod's serverless GPU endpoints for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    /// <summary>Starting, stopping and generating on RunPod pods.</summary>
    public static readonly PermInfo PermUseRunPodPods = Permissions.Register(new PermInfo(
        "use_runpod_pods", "Use RunPod GPU Pods",
        "Allows using RunPod on-demand GPU pods for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    /// <summary>Generating on Vast.ai Serverless workers.</summary>
    public static readonly PermInfo PermUseVastAI = Permissions.Register(new PermInfo(
        "use_vastai", "Use Vast.ai",
        "Allows using Vast.ai's serverless GPU endpoints for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    /// <summary>Starting, stopping and generating on Vast.ai instances.</summary>
    public static readonly PermInfo PermUseVastAIInstances = Permissions.Register(new PermInfo(
        "use_vastai_instances", "Use Vast.ai Instances",
        "Allows using Vast.ai on-demand rented instances for image generation.",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    /// <summary>Viewing workers and status, discovering models, validating, and stopping workers and orphans.</summary>
    public static readonly PermInfo PermCloudStatus = Permissions.Register(new PermInfo(
        "cloudbackends_status", "Cloud Backends Status/Refresh",
        "Allows viewing status of, and refreshing models from, cloud GPU backends (each backend still requires its own provider permission).",
        PermissionDefault.POWERUSERS, CloudPermGroup));

    // ── Extension lifecycle ───────────────────────────────────────────────────

    public CloudBackends()
    {
        // Shown in the Extensions tab until the repo is on the official extension list, which then takes over.
        ExtensionAuthor = "Hartsy";
        Description = "Run SwarmUI generations on RunPod and Vast.ai GPUs, on demand: serverless workers that start when you generate, scale out under load, and shut down when idle, plus rented pods and instances.";
        License = "MIT";
        ReadmeURL = "https://github.com/HartsyAI/SwarmUI-CloudBackends";
        Tags = ["backend", "paid", "cloud"];
    }

    public override void OnPreInit()
    {
        Logs.Init("Initializing Hartsy's Cloud Backends Extension...");
        // Groups the card into one section per provider, with live provider dropdowns and the action buttons.
        ScriptFiles.Add("Assets/cloudbackends.js");
        StyleSheetFiles.Add("Assets/cloudbackends.css");
    }

    public override void OnInit()
    {
        // ── Backend types ─────────────────────────────────────────────────────
        CloudBackendTypes.Init();

        Program.Backends.RegisterBackendType<CloudBackendsBackend>(
            "cloud_backends", "Cloud Backends",
            "Rents GPU capacity from RunPod or Vast.ai. Enable one or more providers in the settings below.",
            CanLoadFast: true);

        // ── API keys ──────────────────────────────────────────────────────────
        RegisterApiKey("runpod_api", "RunPod", "https://console.runpod.io/user/credentials",
            "Enter your RunPod API key. Create one in <a href='https://console.runpod.io/user/credentials' target='_blank'>RunPod Credentials</a>. Used for both Serverless and GPU Pod backends.");

        RegisterApiKey("vastai_api", "Vast.ai", "https://cloud.vast.ai/manage-keys/",
            "Enter your Vast.ai API key. Create one in <a href='https://cloud.vast.ai/manage-keys/' target='_blank'>Vast.ai Keys, API Keys</a>. Used for both Serverless and Instance backends.");

        // ── Remote model providers ────────────────────────────────────────────
        // Instances need none: their attached swarm backend's models already reach the browser through core.
        // The list is shared by all users (the callback has no session); generation is still per user.
        RegisterModelProvider<RunPodServerlessBackend>("runpod_serverless");
        RegisterModelProvider<VastAIBackend>("vastai_serverless");

        // ── Per-user child provisioning ───────────────────────────────────────
        // The only hook before backend matching that knows the user. Serverless children only: instance children come
        // from the explicit start and status routes, because starting one bills.
        RegisterPerUserProvisioning();

        // ── Web API ───────────────────────────────────────────────────────────
        CloudBackendsWebAPI.Register();

        Logs.Info("Cloud Backends extension loaded (one 'Cloud Backends' entry, providers: RunPod Serverless, RunPod GPU Pods, Vast.ai Serverless, Vast.ai Instances).");
    }

    // ── Registration helpers ──────────────────────────────────────────────────

    /// <summary>Last background warm attempt (TickCount64 ms) per parent/user/provider, so per-image PreGenerate stays cheap.</summary>
    static readonly ConcurrentDictionary<string, long> LastWarmAttempt = new();

    /// <summary>Minimum spacing between background warm attempts for one parent/user/provider.</summary>
    const long WarmRetryMs = 60_000;

    /// <summary>Atomically claims the warm-attempt window for a key, so concurrent generations schedule at most one attempt.</summary>
    static bool TryClaimWarmAttempt(string key)
    {
        long now = Environment.TickCount64;
        if (LastWarmAttempt.TryAdd(key, now))
        {
            return true;
        }
        return LastWarmAttempt.TryGetValue(key, out long last) && now - last >= WarmRetryMs && LastWarmAttempt.TryUpdate(key, now, last);
    }

    /// <summary>Creates each user's serverless children when they first generate.</summary>
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
                    // Explicitly targeted: the child must be ready before matching. Its Init only validates credentials (30s cap).
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
                        catch (Exception ex)
                        {
                            lastFail = ex;
                        }
                    }
                    if (!ready && lastFail is not null)
                    {
                        throw lastFail is SwarmReadableErrorException ? lastFail : new SwarmReadableErrorException(lastFail.Message);
                    }
                }
                else
                {
                    // Warms the user's children in the background, throttled since this runs per image.
                    foreach (CloudBackendsBackend parent in parents)
                    {
                        if (parent.GetChildFor(user.UserID, def) is AbstractT2IBackend child && child.Status != BackendStatus.ERRORED)
                        {
                            continue;
                        }
                        if (!TryClaimWarmAttempt($"{parent.BackendData?.ID}/{user.UserID}/{def.Prefix}"))
                        {
                            continue;
                        }
                        _ = Utilities.RunCheckedTask(() => parent.EnsureChildForUser(user, def), "cloud backends child provisioning");
                    }
                }
            }
        };
    }

    /// <summary>Adds an API key type to User Settings.</summary>
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
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Failed to register API key '{keyType}': {ex.Message}");
        }
    }

    /// <summary>Lists a serverless type's discovered models to the browser.</summary>
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
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Failed to register model provider for '{backendTypeId}': {ex.Message}");
        }
    }

}
