using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Net.WebSockets;

namespace Hartsy.Extensions.CloudBackends.WebAPI;

[API.APIClass("API routes for managing cloud GPU backends (RunPod, Vast.ai) added by the Cloud Backends extension.")]
public static class CloudBackendsWebAPI
{
    /// <summary>Registers every API route.</summary>
    public static void Register()
    {
        API.RegisterAPICall(CloudRefreshModels, true, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudGetStatus, false, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudStartPod, true, CloudBackends.PermUseRunPodPods);
        API.RegisterAPICall(CloudStartPodWS, true, CloudBackends.PermUseRunPodPods);
        API.RegisterAPICall(CloudStopPod, true, CloudBackends.PermUseRunPodPods);
        API.RegisterAPICall(CloudGetPodStatus, false, CloudBackends.PermUseRunPodPods);
        API.RegisterAPICall(VastAIStartInstance, true, CloudBackends.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIStartInstanceWS, true, CloudBackends.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIStopInstance, true, CloudBackends.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIGetInstanceStatus, false, CloudBackends.PermUseVastAIInstances);
        API.RegisterAPICall(CloudListProviders, false, Permissions.ViewBackendsList);
        API.RegisterAPICall(CloudListRunPodOptions, false, CloudBackends.PermUseRunPodPods);
        API.RegisterAPICall(CloudListWorkers, false, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudStopWorker, true, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudValidateBackend, false, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudClearModelCache, true, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudListOrphans, false, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(CloudStopOrphan, true, CloudBackends.PermCloudStatus);
        API.RegisterAPICall(VastAIListInstanceOptions, false, CloudBackends.PermUseVastAIInstances);
        API.RegisterAPICall(CloudListEndpoints, false, Permissions.ViewBackendsList);
    }

    /// <summary>An error response: a readable message plus a stable ID for code to check.</summary>
    static JObject Error(string message, string errorId)
    {
        return new JObject { ["error"] = message, ["error_id"] = errorId };
    }

    /// <summary>True if the user has the permission of this backend's provider. Only a permission refusal gives false; any other failure is thrown.</summary>
    static bool HasBackendPermission(ICloudBackend backend, Session session)
    {
        try
        {
            backend.CheckPermission(session);
            return true;
        }
        catch (SwarmReadableErrorException)
        {
            return false;
        }
    }

    /// <summary>The user's permitted serverless backends under a card, optionally for one provider. Accepts the card's ID or a serverless backend's own ID.</summary>
    static List<CloudBackendBase> UserServerless(Session session, int backendId, string provider)
    {
        return [.. Program.Backends.RunningBackendsOfType<CloudBackendBase>()
            .Where(b => b.BackendData?.ID == backendId || b.BackendData?.AbstractParent?.ID == backendId)
            .Where(b => b.OwnerUserId == session.User.UserID && HasBackendPermission(b, session))
            .Where(b => string.IsNullOrWhiteSpace(provider) || CloudBackendsBackend.Providers.Any(d => d.Prefix.Equals(provider, StringComparison.OrdinalIgnoreCase) && d.Type().ID == b.HandlerTypeData.ID))];
    }

    /// <summary>IDs of instances the user's backends have attached, are starting, or still hold, which are therefore not orphans.</summary>
    static HashSet<string> AttachedInstanceIds(Session session)
    {
        return [.. Program.Backends.RunningBackendsOfType<CloudInstanceBackendBase>()
            .Where(b => b.OwnerUserId == session.User.UserID && (b.ChildBackend is not null || b.Starting || b.HoldsInstance) && !string.IsNullOrWhiteSpace(b.Provider?.ActiveInstanceId))
            .Select(b => b.Provider.ActiveInstanceId)];
    }

    /// <summary>The user's instance backend under a card, spawned on their own API key if they have none yet. Accepts the card's ID or the instance backend's own ID.
    /// Throws a readable error if it cannot be found or started.</summary>
    static async Task<T> FindOrSpawnInstanceChild<T>(Session session, int id, CloudBackendsBackend.ProviderDef def) where T : CloudInstanceBackendBase
    {
        T existing = Program.Backends.RunningBackendsOfType<T>()
            .FirstOrDefault(b => (b.BackendData?.ID == id || b.BackendData?.AbstractParent?.ID == id) && b.OwnerUserId == session.User.UserID);
        if (existing is not null)
        {
            return existing;
        }
        CloudBackendsBackend parent = Program.Backends.RunningBackendsOfType<CloudBackendsBackend>()
            .FirstOrDefault(b => b.BackendData?.ID == id) ?? throw new SwarmReadableErrorException($"No running Cloud Backends backend found with ID {id}.");
        AbstractT2IBackend child = await parent.EnsureChildForUser(session.User, def, throwWhenUnavailable: true);
        await CloudBackendsBackend.WaitForChildReady(child);
        return child as T ?? throw new SwarmReadableErrorException($"{def.Label} child of backend #{id} is not the expected type.");
    }

    /// <summary>Starts or stops an instance, streaming the backend's load status as <c>{"status": ...}</c>, then one final <c>{"message": ..., "done": true}</c> or <c>{"error": ...}</c>.
    /// Core clears the load status once a backend has loaded, so streaming it is best-effort and never holds up the action.</summary>
    static async Task InstanceActionInternal<T>(Session session, (int BackendId, CloudBackendsBackend.ProviderDef Def, bool Start) input, Action<JObject> output, bool isWS) where T : CloudInstanceBackendBase
    {
        try
        {
            T backend = await FindOrSpawnInstanceChild<T>(session, input.BackendId, input.Def);
            backend.CheckPermission(session);
            Task action = input.Start ? backend.StartInstanceAsync() : backend.StopInstanceAsync();
            Task<bool> stop = action as Task<bool>;
            int seen = 0;
            while (true)
            {
                List<AbstractBackend.LoadStatus> report = backend.LoadStatusReport;
                if (report is not null)
                {
                    string[] fresh;
                    lock (report)
                    {
                        fresh = [.. report.Skip(seen).Select(s => s.Message)];
                        seen = report.Count;
                    }
                    foreach (string message in fresh)
                    {
                        output(new JObject { ["status"] = message });
                    }
                }
                if (action.IsCompleted)
                {
                    break;
                }
                await Task.WhenAny(action, Task.Delay(500, Program.GlobalProgramCancel));
            }
            await action;
            string result = input.Start ? $"Instance started for backend #{input.BackendId}."
                : stop.Result ? $"Instance stopped for backend #{input.BackendId}."
                : $"No running instance to stop for backend #{input.BackendId}.";
            output(new JObject { ["message"] = result, ["done"] = true });
        }
        catch (SwarmReadableErrorException ex)
        {
            output(Error(ex.Message, "instance_action_failed"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Error($"[CloudBackends] Instance {(input.Start ? "start" : "stop")} for backend #{input.BackendId} failed: {ex.ReadableString()}");
            output(Error($"The instance {(input.Start ? "start" : "stop")} failed: {ex.Message}", "instance_action_failed"));
        }
    }

    /// <summary>Shared by the non-websocket start and stop routes: runs the action and returns only its final message.</summary>
    static async Task<JObject> InstanceActionImpl<T>(Session session, int backend_id, CloudBackendsBackend.ProviderDef def, bool start) where T : CloudInstanceBackendBase
    {
        List<JObject> outputs = await API.RunWebsocketHandlerCallDirect(InstanceActionInternal<T>, session, (backend_id, def, start));
        return outputs.LastOrDefault() ?? Error("Instance action produced no result.", "instance_action_failed");
    }

    /// <summary>Shared by the instance status routes. Retries a reattach that failed at startup, so an instance kept through a restart is not reported as missing.</summary>
    static async Task<JObject> InstanceStatusImpl<T>(Session session, int backend_id, bool force_refresh, CloudBackendsBackend.ProviderDef def) where T : CloudInstanceBackendBase
    {
        try
        {
            T backend = await FindOrSpawnInstanceChild<T>(session, backend_id, def);
            backend.CheckPermission(session);
            if (backend.Provider is null)
            {
                return Error("Backend has no active provider (not yet initialized).", "card_starting");
            }
            await backend.ReattachKeptInstanceAsync();
            CloudInstanceStatus status = await backend.Provider.GetStatusAsync(force_refresh);
            if (status is null)
            {
                return new JObject
                {
                    ["has_instance"] = false,
                    ["message"] = string.IsNullOrWhiteSpace(backend.Provider.ActiveInstanceId)
                        ? "Not created yet. Press Start to create one."
                        : $"'{backend.Provider.ActiveInstanceId}' was not found on {backend.Provider.ProviderName}. Press Start to create a new one."
                };
            }
            return new JObject
            {
                ["has_instance"] = true,
                ["instance_id"] = status.InstanceId,
                ["status"] = status.Status,
                ["gpu_name"] = status.GpuName,
                ["gpu_count"] = status.GpuCount,
                ["cost_per_hour"] = status.CostPerHour,
                ["uptime_seconds"] = status.UptimeSeconds,
                ["allowed_actions"] = JArray.FromObject(status.AllowedActions),
                ["public_url"] = status.PublicUrl
            };
        }
        catch (SwarmReadableErrorException ex)
        {
            return Error(ex.Message, "status_failed");
        }
    }

    [API.APIDescription("Discovers the models of the user's serverless endpoints, on a running worker or a newly leased one (billed until it goes idle). With no card or provider given, every endpoint is refreshed, each on its own worker. Not needed for rented instances.",
        """
            "refreshed": 2, // backends successfully refreshed
            "failed": 0, // backends that errored
            "errors": [{"backend_id": 1, "error": "reason"}],
            "message": "Refreshed 2 cloud backend(s), 0 failed."
        """)]
    public static async Task<JObject> CloudRefreshModels(Session session, [API.APIParameter("ID of the 'Cloud Backends' card to discover for. 0 for every card.")] int backend_id = 0,
        [API.APIParameter("Provider to discover for: 'RunPodServerless_' or 'VastAI_'. Empty for all.")] string provider = "")
    {
        bool InScope(CloudBackendsBackend.ProviderDef def)
        {
            return string.IsNullOrWhiteSpace(provider) || def.Prefix.Equals(provider, StringComparison.OrdinalIgnoreCase);
        }
        foreach (CloudBackendsBackend parent in Program.Backends.RunningBackendsOfType<CloudBackendsBackend>().Where(p => backend_id == 0 || p.BackendData?.ID == backend_id))
        {
            foreach (CloudBackendsBackend.ProviderDef def in CloudBackendsBackend.Providers.Where(d => !d.IsInstance && InScope(d)))
            {
                try
                {
                    AbstractT2IBackend child = await parent.EnsureChildForUser(session.User, def);
                    if (child is not null)
                    {
                        await CloudBackendsBackend.WaitForChildReady(child);
                    }
                }
                catch (SwarmReadableErrorException)
                {
                    // Intentionally empty.
                }
            }
        }
        CloudBackendBase[] backends = [.. (backend_id == 0
                ? Program.Backends.RunningBackendsOfType<CloudBackendBase>().Where(b => b.OwnerUserId == session.User.UserID && HasBackendPermission(b, session))
                : UserServerless(session, backend_id, provider))
            .Where(b => CloudBackendsBackend.Providers.Any(d => !d.IsInstance && InScope(d) && d.Type().ID == b.HandlerTypeData.ID))];
        if (backends.Length is 0)
        {
            CloudBackendBase broken = Program.Backends.AllBackends.Values
                .Select(d => d.AbstractBackend)
                .OfType<CloudBackendBase>()
                .FirstOrDefault(b => b.OwnerUserId == session.User.UserID && b.Status == BackendStatus.ERRORED);
            if (broken is not null)
            {
                string lastStatus = broken.LoadStatusReport?.LastOrDefault()?.Message;
                return Error($"Your {broken.HandlerTypeData?.Name ?? "cloud"} backend failed to start.{(string.IsNullOrWhiteSpace(lastStatus) ? "" : $" {lastStatus}")}", "backend_errored");
            }
            return Error("No serverless cloud backends are available for your user. Enable a serverless provider section and set your API key in User Settings, API Keys.", "no_backend");
        }
        int refreshed = 0, failed = 0;
        JArray errors = [];
        foreach (CloudBackendBase backend in backends)
        {
            try
            {
                Logs.Debug($"[CloudBackends] Refreshing models for backend #{backend.BackendData?.ID} ({backend.CloudProviderName})...");
                await backend.RefreshModelsFromWorkerAsync();
                refreshed++;
            }
            catch (Exception ex)
            {
                Logs.Error($"[CloudBackends] Refresh failed for backend #{backend.BackendData?.ID}: {ex.Message}");
                failed++;
                errors.Add(new JObject { ["backend_id"] = backend.BackendData?.ID, ["error"] = ex.Message });
            }
        }
        string message = $"Refreshed {refreshed} cloud backend(s), {failed} failed.";
        if (failed > 0)
        {
            message += "\n" + string.Join("\n", errors.Select(e => $"Backend #{e["backend_id"]}: {e["error"]}"));
        }
        return new JObject
        {
            ["refreshed"] = refreshed,
            ["failed"] = failed,
            ["errors"] = errors,
            ["message"] = message
        };
    }

    [API.APIDescription("Gets the status of every running cloud backend the user owns and may use, both serverless and rented instances.",
        """
            "backends":
            [
                {
                    "id": 1,
                    "title": "titlehere",
                    "provider": "RunPod Serverless",
                    "kind": "serverless", // or "instance"
                    "status": "RUNNING",
                    // serverless: "endpoint", "model_count", "workers", "workers_starting", "max_workers"
                    // instance: "instance_id", "instance_url", "child_backend_id"
                }
            ],
            "total": 1
        """)]
    public static Task<JObject> CloudGetStatus(Session session)
    {
        JArray statuses = new(Program.Backends.RunningBackendsOfType<AbstractT2IBackend>()
            .OfType<ICloudBackend>()
            .Where(b => b.OwnerUserId == session.User.UserID && HasBackendPermission(b, session))
            .Select(b => b.GetStatusNet()));
        return Task.FromResult(new JObject { ["backends"] = statuses, ["total"] = statuses.Count });
    }

    [API.APIDescription("Starts (or creates) the user's RunPod pod and attaches its Swarm backend. Returns once the pod's SwarmUI answers, which can take minutes.",
        """
            "message": "Instance started for backend #1."
        """)]
    public static Task<JObject> CloudStartPod(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        return InstanceActionImpl<RunPodPodsBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), start: true);
    }

    [API.APIDescription("Websocket variant of CloudStartPod: streams {\"status\": ...} while the pod starts, then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "status": "Starting RunPod GPU Pods instance (up to 900s)..." // repeated, then:
            "message": "Instance started for backend #1.", "done": true
        """)]
    public static async Task<JObject> CloudStartPodWS(WebSocket socket, Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<RunPodPodsBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), true), socket);
        return null;
    }

    [API.APIDescription("Detaches the Swarm backend of the user's RunPod pod, then stops the pod, or deletes it if the card created it and Delete On Stop is on.",
        """
            "message": "Instance stopped for backend #1."
        """)]
    public static Task<JObject> CloudStopPod(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        return InstanceActionImpl<RunPodPodsBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), start: false);
    }

    [API.APIDescription("Gets the live status of the user's RunPod pod, including its cost, uptime and the actions RunPod currently allows. Cached for a few seconds.",
        """
            "has_instance": true, // false with a "message" when no pod is resolved yet
            "instance_id": "abc123",
            "status": "RUNNING",
            "gpu_name": "NVIDIA GeForce RTX 4090",
            "gpu_count": 1,
            "cost_per_hour": 0.44,
            "uptime_seconds": 123,
            "allowed_actions": ["stop", "restart"],
            "public_url": "https://abc123-7801.proxy.runpod.net"
        """)]
    public static Task<JObject> CloudGetPodStatus(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id,
        [API.APIParameter("Skip the status cache, for example right after a Start or Stop.")] bool force_refresh = false)
    {
        return InstanceStatusImpl<RunPodPodsBackend>(session, backend_id, force_refresh, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"));
    }

    [API.APIDescription("Starts (or creates) the user's Vast.ai instance and attaches its Swarm backend. Returns once the instance's SwarmUI answers, which can take minutes.",
        """
            "message": "Instance started for backend #1."
        """)]
    public static Task<JObject> VastAIStartInstance(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        return InstanceActionImpl<VastAIInstanceBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), start: true);
    }

    [API.APIDescription("Websocket variant of VastAIStartInstance: streams {\"status\": ...} while the instance starts, then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "status": "Starting Vast.ai Instances instance (up to 900s)..." // repeated, then:
            "message": "Instance started for backend #1.", "done": true
        """)]
    public static async Task<JObject> VastAIStartInstanceWS(WebSocket socket, Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<VastAIInstanceBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), true), socket);
        return null;
    }

    [API.APIDescription("Detaches the Swarm backend of the user's Vast.ai instance, then stops the instance, or destroys it if the card created it and Delete On Stop is on.",
        """
            "message": "Instance stopped for backend #1."
        """)]
    public static Task<JObject> VastAIStopInstance(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id)
    {
        return InstanceActionImpl<VastAIInstanceBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), start: false);
    }

    [API.APIDescription("Gets the live status of the user's Vast.ai instance, including its cost and uptime. Cached for a few seconds.",
        """
            "has_instance": true, // false with a "message" when no instance is resolved yet
            "instance_id": "12345678",
            "status": "running",
            "gpu_name": "RTX 4090",
            "gpu_count": 1,
            "cost_per_hour": 0.31,
            "uptime_seconds": 123,
            "allowed_actions": [], // Vast publishes no allowed-actions list
            "public_url": "https://1.2.3.4:45678"
        """)]
    public static Task<JObject> VastAIGetInstanceStatus(Session session, [API.APIParameter("ID of the 'Cloud Backends' card.")] int backend_id,
        [API.APIParameter("Skip the status cache, for example right after a Start or Stop.")] bool force_refresh = false)
    {
        return InstanceStatusImpl<VastAIInstanceBackend>(session, backend_id, force_refresh, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"));
    }

    [API.APIDescription("Lists what the user's RunPod account can use right now, for the card's dropdowns: GPU types with availability and price, network volumes, data centers, templates and pods.",
        """
            "cloud": "SECURE",
            "gpus": [{"id": "...", "name": "...", "memory_gb": 24, "price_per_hr": 0.44, "max_count": 8, "availability": "HIGH", "data_centers": ["..."]}],
            "network_volumes": [{"id": "...", "name": "...", "size_gb": 100, "data_center": "..."}],
            "templates": [{"id": "...", "name": "...", "image": "..."}], // pod templates only
            "data_centers": [{"id": "...", "name": "..."}],
            "pods": [{"id": "...", "name": "...", "status": "...", "gpu": "...", "cost_per_hr": 0.44}]
        """)]
    public static async Task<JObject> CloudListRunPodOptions(Session session, [API.APIParameter("'SECURE' (default) or 'COMMUNITY'.")] string cloud_type = "SECURE")
    {
        string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return Error("No RunPod API key set. Add one in User Settings, API Keys, RunPod, then reopen this form.", "no_api_key");
        }
        string cloud = string.Equals(cloud_type, "COMMUNITY", StringComparison.OrdinalIgnoreCase) ? "COMMUNITY" : "SECURE";
        using RunPodPodsProvider provider = new(key, new RunPodPodPlan { CloudType = cloud });
        return await provider.ListAccountOptionsAsync(cloud);
    }

    [API.APIDescription("Lists what the user's Vast.ai account can use right now, for the card's dropdowns: GPU models on offer (single-GPU machines) with their lowest price, network volumes, instances and templates.",
        """
            "gpus": [{"name": "RTX 4090", "gpu_ram": 24, "min_price": 0.31, "offers": 42}],
            "network_volumes": [{"id": "...", "name": "...", "size_gb": 100}],
            "instances": [{"id": "...", "label": "...", "status": "running", "gpu": "RTX 4090", "num_gpus": 1}],
            "templates": [{"id": "<template hash>", "name": "...", "image": "..."}]
        """)]
    public static async Task<JObject> VastAIListInstanceOptions(Session session)
    {
        string key = session.User.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return Error("No Vast.ai API key set. Add one in User Settings, API Keys, Vast.ai, then reopen this form.", "no_api_key");
        }
        using VastAIInstanceProvider provider = new(key, new VastAIInstancePlan());
        return await provider.ListAccountOptionsAsync();
    }

    [API.APIDescription("Lists the user's serverless endpoints on one provider, for the card's dropdown. 'id' is the value the Endpoint setting takes: the endpoint's ID on RunPod, its name on Vast.ai.",
        """
            "endpoints": [{"id": "...", "name": "...", "image": "...", "max_workers": 3}]
        """)]
    public static async Task<JObject> CloudListEndpoints(Session session, [API.APIParameter("'RunPodServerless_' or 'VastAI_'.")] string provider)
    {
        (string keyType, string keyName, PermInfo perm) = provider switch
        {
            "RunPodServerless_" => ("runpod_api", "RunPod", CloudBackends.PermUseRunPodServerless),
            "VastAI_" => ("vastai_api", "Vast.ai", CloudBackends.PermUseVastAI),
            _ => (null, null, null)
        };
        if (keyType is null)
        {
            return Error($"Unknown serverless provider '{provider}'.", "unknown_provider");
        }
        if (!session.User.HasPermission(perm))
        {
            return Error($"You do not have permission to use {keyName} serverless backends.", "no_permission");
        }
        string key = session.User.GetGenericData(keyType, "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return Error($"No {keyName} API key set. Add one in User Settings, API Keys, {keyName}, then reopen this form.", "no_api_key");
        }
        using ICloudProvider cloud = provider == "VastAI_" ? new VastAIProvider(key, "") : new RunPodServerlessProvider(key, "");
        return new JObject { ["endpoints"] = await cloud.ListEndpointsAsync(Program.GlobalProgramCancel) };
    }

    [API.APIDescription("Lists the supported cloud providers and what the card needs to show each: settings prefix, setup guide, Advanced settings, and for instance providers the suggested images and route names.",
        """
            "providers":
            [
                {
                    "prefix": "RunPodPods_",
                    "label": "RunPod GPU Pods",
                    "key_type": "runpod_api",
                    "is_instance": true,
                    "guide_url": "https://github.com/HartsyAI/SwarmUI-CloudBackends/blob/main/docs/setup/runpod-pods.md",
                    "advanced_fields": ["RunPodPods_TemplateId", "..."],
                    "images": [{"image": "kalebbroo/swarmui-worker-runpod:2.0.0-comfyui", "label": "Hartsy worker, ComfyUI"}], // only for instance providers
                    "item_label": "Pod", // only for instance providers
                    "start_call": "CloudStartPod",
                    "start_call_ws": "CloudStartPodWS",
                    "stop_call": "CloudStopPod",
                    "status_call": "CloudGetPodStatus"
                }
            ]
        """)]
    public static Task<JObject> CloudListProviders(Session session)
    {
        JArray providers = [];
        foreach (CloudBackendsBackend.ProviderDef def in CloudBackendsBackend.Providers)
        {
            JObject entry = new()
            {
                ["prefix"] = def.Prefix,
                ["label"] = def.Label,
                ["key_type"] = def.KeyType,
                ["is_instance"] = def.IsInstance,
                ["guide_url"] = CloudBackendsBackend.GuideBaseUrl + def.Guide,
                ["advanced_fields"] = new JArray(CloudBackendsBackend.AdvancedFields.Where(f => f.StartsWith(def.Prefix)))
            };
            if (CloudBackendTypes.WorkerImages.TryGetValue(def.Prefix, out (string Image, string Label)[] images))
            {
                entry["images"] = new JArray(images.Select(i => new JObject { ["image"] = i.Image, ["label"] = i.Label }));
            }
            if (def.Prefix == "RunPodPods_")
            {
                entry["item_label"] = "Pod";
                entry["start_call"] = "CloudStartPod";
                entry["start_call_ws"] = "CloudStartPodWS";
                entry["stop_call"] = "CloudStopPod";
                entry["status_call"] = "CloudGetPodStatus";
            }
            else if (def.Prefix == "VastAIInstance_")
            {
                entry["item_label"] = "Instance";
                entry["start_call"] = "VastAIStartInstance";
                entry["start_call_ws"] = "VastAIStartInstanceWS";
                entry["stop_call"] = "VastAIStopInstance";
                entry["status_call"] = "VastAIGetInstanceStatus";
            }
            providers.Add(entry);
        }
        return Task.FromResult(new JObject { ["providers"] = providers });
    }

    [API.APIDescription("Lists the user's leased serverless workers under a card, one entry per provider. Never includes access tokens.",
        """
            "backends":
            [
                {
                    "backend_id": -12, // the user's hidden serverless backend
                    "provider": "RunPod Serverless",
                    "endpoint": "abc123",
                    "max_workers": 2,
                    "workers_starting": 0,
                    "workers":
                    [
                        {
                            "worker_id": "podid",
                            "lease_id": "job-or-session-id",
                            "public_url": "https://podid-7801.proxy.runpod.net",
                            "age_seconds": 95,
                            "in_use": true,
                            "child_backend_id": -13,
                            "backends": [{"id": -14, "status": "running", "usages": 1, "max_usages": 2}]
                        }
                    ]
                }
            ]
        """)]
    public static Task<JObject> CloudListWorkers(Session session, [API.APIParameter("ID of the 'Cloud Backends' card (or of one of its serverless backends).")] int backend_id,
        [API.APIParameter("Provider to list: 'RunPodServerless_' or 'VastAI_'. Empty for all.")] string provider = "")
    {
        JArray backends = [];
        foreach (CloudBackendBase backend in UserServerless(session, backend_id, provider))
        {
            JObject status = backend.GetStatusNet();
            backends.Add(new JObject
            {
                ["backend_id"] = backend.BackendData?.ID,
                ["provider"] = backend.CloudProviderName,
                ["endpoint"] = status["endpoint"],
                ["max_workers"] = status["max_workers"],
                ["workers_starting"] = status["workers_starting"],
                ["workers"] = backend.DescribeWorkers()
            });
        }
        return Task.FromResult(new JObject { ["backends"] = backends });
    }

    [API.APIDescription("Releases one leased serverless worker now, which stops its billing. Anything it is generating fails.",
        """
            "success": true
        """)]
    public static async Task<JObject> CloudStopWorker(Session session, [API.APIParameter("ID of the 'Cloud Backends' card (or of one of its serverless backends).")] int backend_id,
        [API.APIParameter("Worker ID or lease ID, as listed by CloudListWorkers.")] string worker_id)
    {
        foreach (CloudBackendBase backend in UserServerless(session, backend_id, ""))
        {
            WorkerSlot slot = backend.FindSlot(worker_id);
            if (slot is not null)
            {
                backend.CheckPermission(session);
                await backend.RemoveSlotAsync(slot, $"stopped by {session.User.UserID}");
                return new JObject { ["success"] = true };
            }
        }
        return Error($"No leased worker '{worker_id}' of yours was found under backend #{backend_id}.", "worker_not_found");
    }

    [API.APIDescription("Checks the configuration of the user's serverless endpoints on the provider against the card's settings. Changes nothing and bills nothing.",
        """
            "results":
            [
                {
                    "provider": "RunPod Serverless",
                    "backend_id": -12,
                    "findings": [{"level": "error", "message": "..."}] // empty when all is well
                }
            ]
        """)]
    public static async Task<JObject> CloudValidateBackend(Session session, [API.APIParameter("ID of the 'Cloud Backends' card (or of one of its serverless backends).")] int backend_id)
    {
        foreach (CloudBackendsBackend parent in Program.Backends.RunningBackendsOfType<CloudBackendsBackend>().Where(p => p.BackendData?.ID == backend_id))
        {
            foreach (CloudBackendsBackend.ProviderDef def in CloudBackendsBackend.Providers.Where(d => !d.IsInstance && parent.IsProviderEnabled(d)))
            {
                try
                {
                    AbstractT2IBackend child = await parent.EnsureChildForUser(session.User, def);
                    if (child is not null)
                    {
                        await CloudBackendsBackend.WaitForChildReady(child);
                    }
                }
                catch (SwarmReadableErrorException ex)
                {
                    Logs.Debug($"[CloudBackends] Could not prepare {def.Label} for validation: {ex.Message}");
                }
            }
        }
        List<CloudBackendBase> backends = UserServerless(session, backend_id, "");
        if (backends.Count == 0 && Program.Backends.AllBackends.TryGetValue(backend_id, out BackendHandler.BackendData card) && card.AbstractBackend is CloudBackendsBackend && card.AbstractBackend.Status is BackendStatus.WAITING or BackendStatus.LOADING)
        {
            return Error("This Cloud Backends card is still starting. Try again in a moment.", "card_starting");
        }
        if (backends.Count == 0)
        {
            return Error("You have no running serverless backend under this card. Enable a serverless section and set your API key in User Settings, then generate or use Discover models once.", "no_backend");
        }
        JArray results = [];
        foreach (CloudBackendBase backend in backends)
        {
            JObject entry = new() { ["provider"] = backend.CloudProviderName, ["backend_id"] = backend.BackendData?.ID };
            try
            {
                using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
                entry["findings"] = await backend.CheckConfigurationAsync(cancel.Token);
            }
            catch (SwarmReadableErrorException ex)
            {
                entry["findings"] = new JArray(new JObject { ["level"] = "error", ["message"] = ex.Message });
            }
            results.Add(entry);
        }
        return new JObject { ["results"] = results };
    }

    [API.APIDescription("Forgets the remembered model list of the user's serverless endpoints, so it is learned again from the next worker.",
        """
            "success": true,
            "cleared": 1
        """)]
    public static Task<JObject> CloudClearModelCache(Session session, [API.APIParameter("ID of the 'Cloud Backends' card (or of one of its serverless backends).")] int backend_id,
        [API.APIParameter("Provider to clear: 'RunPodServerless_' or 'VastAI_'. Empty for all.")] string provider = "")
    {
        List<CloudBackendBase> backends = UserServerless(session, backend_id, provider);
        foreach (CloudBackendBase backend in backends)
        {
            backend.ClearModelCache();
        }
        return Task.FromResult(new JObject { ["success"] = true, ["cleared"] = backends.Count });
    }

    [API.APIDescription("Lists running pods and instances this extension created on the user's accounts that no card has attached, for example after SwarmUI stopped uncleanly. They bill until stopped with CloudStopOrphan.",
        """
            "orphans":
            [
                {"provider": "runpod", "id": "podid", "name": "swarmui-cloudbackends-3", "status": "RUNNING", "cost_per_hr": 0.44}
            ],
            "errors": [{"provider": "vastai", "error": "..."}] // providers that could not be checked
        """)]
    public static async Task<JObject> CloudListOrphans(Session session)
    {
        HashSet<string> attached = AttachedInstanceIds(session);
        JArray orphans = [];
        JArray errors = [];
        string runpodKey = session.User.GetGenericData("runpod_api", "key")?.Trim();
        if (!string.IsNullOrEmpty(runpodKey) && session.User.HasPermission(CloudBackends.PermUseRunPodPods))
        {
            try
            {
                using RunPodPodsProvider provider = new(runpodKey, new RunPodPodPlan());
                foreach (JObject pod in await provider.ListManagedRunningAsync(CloudInstanceBackendBase.KnownInstances(session.User, nameof(RunPodPodsBackend))))
                {
                    if (!attached.Contains(pod["id"]?.ToString() ?? ""))
                    {
                        pod["provider"] = "runpod";
                        orphans.Add(pod);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new JObject { ["provider"] = "runpod", ["error"] = ex.Message });
            }
        }
        string vastKey = session.User.GetGenericData("vastai_api", "key")?.Trim();
        if (!string.IsNullOrEmpty(vastKey) && session.User.HasPermission(CloudBackends.PermUseVastAIInstances))
        {
            try
            {
                using VastAIInstanceProvider provider = new(vastKey, new VastAIInstancePlan());
                foreach (JObject instance in await provider.ListManagedRunningAsync(CloudInstanceBackendBase.KnownInstances(session.User, nameof(VastAIInstanceBackend))))
                {
                    if (!attached.Contains(instance["id"]?.ToString() ?? ""))
                    {
                        instance["provider"] = "vastai";
                        orphans.Add(instance);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new JObject { ["provider"] = "vastai", ["error"] = ex.Message });
            }
        }
        return new JObject { ["orphans"] = orphans, ["errors"] = errors };
    }

    [API.APIDescription("Stops one orphaned pod or instance listed by CloudListOrphans. Its disk is kept.",
        """
            "success": true
        """)]
    public static async Task<JObject> CloudStopOrphan(Session session, [API.APIParameter("'runpod' or 'vastai'.")] string provider,
        [API.APIParameter("Pod or instance ID, as listed by CloudListOrphans.")] string resource_id)
    {
        if (AttachedInstanceIds(session).Contains(resource_id))
        {
            return Error("That instance is attached to one of your Cloud Backends cards; stop it from the card instead.", "not_orphan");
        }
        if (provider == "runpod")
        {
            string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
            if (string.IsNullOrEmpty(key) || !session.User.HasPermission(CloudBackends.PermUseRunPodPods))
            {
                return Error("You need a RunPod API key and the RunPod GPU Pods permission to do this.", "no_api_key");
            }
            using RunPodPodsProvider pods = new(key, new RunPodPodPlan());
            if (!(await pods.ListManagedRunningAsync(CloudInstanceBackendBase.KnownInstances(session.User, nameof(RunPodPodsBackend)))).Any(p => p["id"]?.ToString() == resource_id))
            {
                return Error($"No running pod '{resource_id}' created by Cloud Backends was found on your RunPod account.", "orphan_not_found");
            }
            await pods.StopPodByIdAsync(resource_id);
            return new JObject { ["success"] = true };
        }
        if (provider == "vastai")
        {
            string key = session.User.GetGenericData("vastai_api", "key")?.Trim();
            if (string.IsNullOrEmpty(key) || !session.User.HasPermission(CloudBackends.PermUseVastAIInstances))
            {
                return Error("You need a Vast.ai API key and the Vast.ai Instances permission to do this.", "no_api_key");
            }
            using VastAIInstanceProvider instances = new(key, new VastAIInstancePlan());
            if (!(await instances.ListManagedRunningAsync(CloudInstanceBackendBase.KnownInstances(session.User, nameof(VastAIInstanceBackend)))).Any(i => i["id"]?.ToString() == resource_id))
            {
                return Error($"No running instance '{resource_id}' created by Cloud Backends was found on your Vast.ai account.", "orphan_not_found");
            }
            await instances.StopInstanceByIdAsync(resource_id);
            return new JObject { ["success"] = true };
        }
        return Error($"Unknown provider '{provider}'. Use 'runpod' or 'vastai'.", "invalid_provider");
    }
}
