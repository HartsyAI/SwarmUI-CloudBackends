using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.CloudBackends.WebAPI;

[API.APIClass("API routes for managing cloud GPU backends (RunPod, Vast.ai) added by the Cloud Backends extension.")]
public static class CloudBackendsWebAPI
{
    public static void Register()
    {
        API.RegisterAPICall(CloudRefreshModels, true, CloudBackendsExtension.PermCloudStatus);
        API.RegisterAPICall(CloudGetStatus, false, CloudBackendsExtension.PermCloudStatus);
        API.RegisterAPICall(CloudStartPod, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudStartPodWS, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudStopPod, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudStopPodWS, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudGetPodStatus, false, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(VastAIStartInstance, true, CloudBackendsExtension.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIStartInstanceWS, true, CloudBackendsExtension.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIStopInstance, true, CloudBackendsExtension.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIStopInstanceWS, true, CloudBackendsExtension.PermUseVastAIInstances);
        API.RegisterAPICall(VastAIGetInstanceStatus, false, CloudBackendsExtension.PermUseVastAIInstances);
        API.RegisterAPICall(CloudListProviders, false, Permissions.ViewBackendsList);
        API.RegisterAPICall(CloudDebugInvalidateSession, true, Permissions.EditBackends);
        API.RegisterAPICall(CloudListRunPodOptions, false, Permissions.EditBackends);
        API.RegisterAPICall(VastAIListInstanceOptions, false, Permissions.EditBackends);
    }

    /// <summary>True if the session's user may act on this backend (each provider defines its own permission).</summary>
    static bool HasBackendPermission(ICloudBackend backend, Session session)
    {
        // Only a permission refusal means "not yours" - anything else is a real fault and must not be
        // silently reported to the user as "no cloud backends are running".
        try { backend.CheckPermission(session); return true; }
        catch (SwarmReadableErrorException) { return false; }
    }

    /// <summary>
    /// Finds (or spawns) the requesting user's instance-renting child backend of type
    /// <typeparamref name="T"/> for a given ID. The frontend only ever sees the parent "Cloud Backends"
    /// backend's ID, never a hidden child's own (negative, nonreal) ID, so this matches either: the
    /// child's own ID directly (in case a caller ever has it), or - the normal case - the requesting
    /// user's child whose <see cref="BackendHandler.BackendData.AbstractParent"/> is the given parent.
    /// When the user has no child yet, one is spawned on their own API key and waited for - these
    /// routes are the explicit user actions that instance children spawn from.
    /// Throws <see cref="SwarmReadableErrorException"/> with an actionable message on any failure.
    /// </summary>
    static async Task<T> FindOrSpawnInstanceChild<T>(Session session, int id, CloudBackendsBackend.ProviderDef def) where T : CloudInstanceBackendBase
    {
        T existing = Program.Backends.RunningBackendsOfType<T>()
            .FirstOrDefault(b => (b.BackendData?.ID == id || b.BackendData?.AbstractParent?.ID == id) && b.OwnerUserId == session.User.UserID);
        if (existing is not null)
        {
            return existing;
        }
        CloudBackendsBackend parent = Program.Backends.RunningBackendsOfType<CloudBackendsBackend>()
            .FirstOrDefault(b => b.BackendData?.ID == id)
            ?? throw new SwarmReadableErrorException($"No running Cloud Backends backend found with ID {id}.");
        // EnsureChildForUser throws readably when the section is disabled or the user has no key; an
        // existing-but-errored child comes back as-is and WaitForChildReady surfaces its real reason.
        AbstractT2IBackend child = await parent.EnsureChildForUser(session.User, def, throwWhenUnavailable: true);
        await CloudBackendsBackend.WaitForChildReady(child);
        return child as T ?? throw new SwarmReadableErrorException($"{def.Label} child of backend #{id} is not the expected type.");
    }

    /// <summary>
    /// Runs a start or stop as a streaming operation: the backend's own load-status lines are emitted
    /// as <c>{"status": ...}</c> progress messages while the action runs (a cold pod start takes
    /// minutes), then one final <c>{"message": ..., "done": true}</c> or <c>{"error": ...}</c>.
    /// Shared by the WS routes (live streaming) and the plain routes (which return only the final message).
    /// </summary>
    static async Task InstanceActionInternal<T>(Session session, (int BackendId, CloudBackendsBackend.ProviderDef Def, bool Start) input, Action<JObject> output, bool isWS) where T : CloudInstanceBackendBase
    {
        try
        {
            T backend = await FindOrSpawnInstanceChild<T>(session, input.BackendId, input.Def);
            backend.CheckPermission(session);
            int seen;
            lock (backend.LoadStatusReport)
            {
                seen = backend.LoadStatusReport.Count;
            }
            Task action = input.Start ? backend.StartInstanceAsync() : backend.StopInstanceAsync();
            while (true)
            {
                string[] fresh;
                lock (backend.LoadStatusReport)
                {
                    fresh = [.. backend.LoadStatusReport.Skip(seen).Select(s => s.Message)];
                    seen = backend.LoadStatusReport.Count;
                }
                foreach (string message in fresh)
                {
                    output(new JObject { ["status"] = message });
                }
                if (action.IsCompleted)
                {
                    break;
                }
                await Task.WhenAny(action, Task.Delay(500, Program.GlobalProgramCancel));
            }
            await action;
            output(new JObject { ["message"] = $"Instance {(input.Start ? "started" : "stopped")} for backend #{input.BackendId}.", ["done"] = true });
        }
        catch (SwarmReadableErrorException ex)
        {
            output(new JObject { ["error"] = ex.Message });
        }
    }

    /// <summary>Shared implementation behind the per-provider plain (non-WS) start/stop routes: run the streaming action, return only its final message.</summary>
    static async Task<JObject> InstanceActionImpl<T>(Session session, int backend_id, CloudBackendsBackend.ProviderDef def, bool start) where T : CloudInstanceBackendBase
    {
        List<JObject> outputs = await API.RunWebsocketHandlerCallDirect(InstanceActionInternal<T>, session, (backend_id, def, start));
        return outputs.LastOrDefault() ?? new JObject { ["error"] = "Instance action produced no result." };
    }

    /// <summary>Shared implementation behind the per-provider instance-status routes.</summary>
    static async Task<JObject> InstanceStatusImpl<T>(Session session, int backend_id, bool force_refresh, CloudBackendsBackend.ProviderDef def) where T : CloudInstanceBackendBase
    {
        try
        {
            T backend = await FindOrSpawnInstanceChild<T>(session, backend_id, def);
            backend.CheckPermission(session);
            if (backend.Provider is null)
            {
                return new JObject { ["error"] = "Backend has no active provider (not yet initialized)." };
            }
            CloudInstanceStatus status = await backend.Provider.GetStatusAsync(force_refresh);
            if (status is null)
            {
                return new JObject { ["has_instance"] = false, ["message"] = "No instance resolved yet - it is created on first Start." };
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
            return new JObject { ["error"] = ex.Message };
        }
    }

    [API.APIDescription("Triggers a model refresh from the remote workers of all running serverless cloud backends the user may access (rented-instance backends mirror models through core's own Swarm backend instead).",
        """
            "refreshed": 2, // backends successfully refreshed
            "failed": 0, // backends that errored
            "errors": [{"backend_id": 1, "error": "reason"}],
            "message": "Refreshed 2 cloud backend(s), 0 failed."
        """)]
    public static async Task<JObject> CloudRefreshModels(Session session)
    {
        // An explicit refresh is a deliberate user action, so it may spawn the user's serverless
        // children first (each on the user's own key) - then refresh only the user's own children.
        // Each child must finish Init before the RUNNING filter below, or a first-ever refresh
        // would see them all as still-loading and wrongly report "none available"; a child that
        // fails Init is tolerated here and surfaces its real reason via the ERRORED fallback below.
        foreach (CloudBackendsBackend parent in Program.Backends.RunningBackendsOfType<CloudBackendsBackend>())
        {
            foreach (CloudBackendsBackend.ProviderDef def in CloudBackendsBackend.Providers.Where(d => !d.IsInstance))
            {
                try
                {
                    AbstractT2IBackend child = await parent.EnsureChildForUser(session.User, def);
                    if (child is not null)
                    {
                        await CloudBackendsBackend.WaitForChildReady(child);
                    }
                }
                catch (SwarmReadableErrorException) { }
            }
        }
        CloudBackendBase[] backends = [.. Program.Backends.RunningBackendsOfType<CloudBackendBase>()
            .Where(b => b.OwnerUserId == session.User.UserID && HasBackendPermission(b, session))];
        if (backends.Length is 0)
        {
            // A child may exist but have failed Init (bad key, bad endpoint) - its real reason is far
            // more actionable than a generic "none available".
            CloudBackendBase broken = Program.Backends.AllBackends.Values
                .Select(d => d.AbstractBackend)
                .OfType<CloudBackendBase>()
                .FirstOrDefault(b => b.OwnerUserId == session.User.UserID && b.Status == BackendStatus.ERRORED);
            if (broken is not null)
            {
                string lastStatus = broken.LoadStatusReport?.LastOrDefault()?.Message;
                return new JObject { ["error"] = $"Your {broken.HandlerTypeData?.Name ?? "cloud"} backend failed to start.{(string.IsNullOrWhiteSpace(lastStatus) ? "" : $" {lastStatus}")}" };
            }
            return new JObject { ["error"] = "No serverless cloud backends are available for your user. Enable a serverless provider section and set your API key in User Settings, API Keys." };
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
                // Carry the reason back to the caller - a bare failure count leaves the user with
                // nothing to act on, and this is usually a fixable worker misconfiguration.
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

    [API.APIDescription("Gets status for every running cloud backend the user may access, across all providers - serverless workers and rented instances alike.",
        """
            "backends":
            [
                {
                    "id": 1,
                    "title": "titlehere",
                    "provider": "RunPod Serverless",
                    "kind": "serverless", // or "instance"
                    "status": "RUNNING",
                    // serverless: "endpoint", "model_count", "worker_id", "worker_url", "max_concurrent", "auto_refresh"
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

    [API.APIDescription("Starts (or creates, per settings) the cloud pod behind a RunPod GPU Pods backend and attaches its Swarm backend. Blocks until the pod's SwarmUI is reachable, which can take minutes on a cold start.",
        """
            "message": "Instance started for backend #1."
        """)]
    public static Task<JObject> CloudStartPod(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on.")] int backend_id)
    {
        return InstanceActionImpl<RunPodPodsBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), start: true);
    }

    [API.APIDescription("Websocket variant of CloudStartPod: streams the backend's status lines as {\"status\": ...} while the pod starts (minutes on a cold start), then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "status": "Starting RunPod GPU Pods instance (up to 900s)..." // repeated, then:
            "message": "Instance started for backend #1.", "done": true
        """)]
    public static async Task<JObject> CloudStartPodWS(System.Net.WebSockets.WebSocket socket, Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<RunPodPodsBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), true), socket);
        return null;
    }

    [API.APIDescription("Stops the cloud pod behind a RunPod GPU Pods backend, detaching its Swarm backend first. The pod stays stopped until started again.",
        """
            "message": "Instance stopped for backend #1."
        """)]
    public static Task<JObject> CloudStopPod(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on.")] int backend_id)
    {
        return InstanceActionImpl<RunPodPodsBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), start: false);
    }

    [API.APIDescription("Websocket variant of CloudStopPod: streams status lines while the pod stops, then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "message": "Instance stopped for backend #1.", "done": true
        """)]
    public static async Task<JObject> CloudStopPodWS(System.Net.WebSockets.WebSocket socket, Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<RunPodPodsBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"), false), socket);
        return null;
    }

    [API.APIDescription("Live status of the pod behind a RunPod GPU Pods backend - state, GPU, cost/hr, uptime, and which actions RunPod currently allows on it. Cached briefly server-side so a UI poll does not hammer RunPod's API.",
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
    public static Task<JObject> CloudGetPodStatus(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to query.")] int backend_id,
        [API.APIParameter("If true, bypass the short server-side status cache (e.g. right after a Start/Stop).")] bool force_refresh = false)
    {
        return InstanceStatusImpl<RunPodPodsBackend>(session, backend_id, force_refresh, CloudBackendsBackend.ProviderByPrefix("RunPodPods_"));
    }

    [API.APIDescription("Starts (or creates, per settings) the Vast.ai instance behind a Vast.ai Instances backend and attaches its Swarm backend. Blocks until the instance's SwarmUI is reachable, which can take minutes on a cold start.",
        """
            "message": "Instance started for backend #1."
        """)]
    public static Task<JObject> VastAIStartInstance(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on.")] int backend_id)
    {
        return InstanceActionImpl<VastAIInstanceBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), start: true);
    }

    [API.APIDescription("Websocket variant of VastAIStartInstance: streams the backend's status lines as {\"status\": ...} while the instance starts (minutes on a cold start), then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "status": "Starting Vast.ai Instances instance (up to 900s)..." // repeated, then:
            "message": "Instance started for backend #1.", "done": true
        """)]
    public static async Task<JObject> VastAIStartInstanceWS(System.Net.WebSockets.WebSocket socket, Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<VastAIInstanceBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), true), socket);
        return null;
    }

    [API.APIDescription("Stops the Vast.ai instance behind a Vast.ai Instances backend, detaching its Swarm backend first. The instance stays stopped until started again.",
        """
            "message": "Instance stopped for backend #1."
        """)]
    public static Task<JObject> VastAIStopInstance(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on.")] int backend_id)
    {
        return InstanceActionImpl<VastAIInstanceBackend>(session, backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), start: false);
    }

    [API.APIDescription("Websocket variant of VastAIStopInstance: streams status lines while the instance stops, then a final {\"message\": ..., \"done\": true} or {\"error\": ...}.",
        """
            "message": "Instance stopped for backend #1.", "done": true
        """)]
    public static async Task<JObject> VastAIStopInstanceWS(System.Net.WebSockets.WebSocket socket, Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on.")] int backend_id)
    {
        await API.RunWebsocketHandlerCallWS(InstanceActionInternal<VastAIInstanceBackend>, session, (backend_id, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"), false), socket);
        return null;
    }

    [API.APIDescription("Live status of the instance behind a Vast.ai Instances backend - state, GPU, cost/hr, uptime. Cached briefly server-side so a UI poll does not hammer Vast's API.",
        """
            "has_instance": true, // false with a "message" when no instance is resolved yet
            "instance_id": "12345678",
            "status": "running",
            "gpu_name": "RTX 4090",
            "gpu_count": 1,
            "cost_per_hour": 0.31,
            "uptime_seconds": 123,
            "allowed_actions": [], // Vast publishes no allowed-actions list
            "public_url": "http://1.2.3.4:45678"
        """)]
    public static Task<JObject> VastAIGetInstanceStatus(Session session,
        [API.APIParameter("ID of the 'Cloud Backends' backend whose Vast.ai Instances section to query.")] int backend_id,
        [API.APIParameter("If true, bypass the short server-side status cache (e.g. right after a Start/Stop).")] bool force_refresh = false)
    {
        return InstanceStatusImpl<VastAIInstanceBackend>(session, backend_id, force_refresh, CloudBackendsBackend.ProviderByPrefix("VastAIInstance_"));
    }

    [API.APIDescription("Lists what the user's RunPod account can actually use right now: GPU types with live availability and price, network volumes, data centers, templates, and pods. Exists so the backend settings form can offer real choices instead of asking someone to type an exact GPU name and hope it is spelled right and in stock.",
        """
            "cloud": "SECURE",
            "gpus": [{"id": "...", "name": "...", "memory_gb": 24, "price_per_hr": 0.44, "max_count": 8, "availability": "HIGH", "data_centers": ["..."]}],
            "network_volumes": [{"id": "...", "name": "...", "size_gb": 100, "data_center": "..."}],
            "templates": [{"id": "...", "name": "...", "image": "..."}],
            "data_centers": [{"id": "...", "name": "..."}],
            "pods": [{"id": "...", "name": "...", "status": "...", "gpu": "...", "cost_per_hr": 0.44}]
        """)]
    public static async Task<JObject> CloudListRunPodOptions(Session session,
        [API.APIParameter("Which RunPod cloud to list for: 'SECURE' (default) or 'COMMUNITY'.")] string cloud_type = "SECURE")
    {
        string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return new JObject { ["error"] = "No RunPod API key set. Add one in User Settings, API Keys, RunPod, then reopen this form." };
        }
        string cloud = string.Equals(cloud_type, "COMMUNITY", StringComparison.OrdinalIgnoreCase) ? "COMMUNITY" : "SECURE";
        using RunPodPodsProvider provider = new(key, new RunPodPodPlan { CloudType = cloud });
        return await provider.ListAccountOptionsAsync(cloud);
    }

    [API.APIDescription("Lists what the user's Vast.ai account can actually rent right now: on-demand offers (GPU, price, location) and the account's own network volumes. Same purpose as CloudListRunPodOptions.",
        """
            "offers": [{"id": "...", "gpu_name": "RTX 4090", "num_gpus": 1, "gpu_ram": 24, "disk_space": 100, "geolocation": "US", "reliability": 0.99, "dph_total": 0.31}],
            "network_volumes": [{"id": "...", "name": "...", "size_gb": 100}]
        """)]
    public static async Task<JObject> VastAIListInstanceOptions(Session session)
    {
        string key = session.User.GetGenericData("vastai_api", "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return new JObject { ["error"] = "No Vast.ai API key set. Add one in User Settings, API Keys, Vast.ai, then reopen this form." };
        }
        using VastAIInstanceProvider provider = new(key, new VastAIInstancePlan());
        return await provider.ListAccountOptionsAsync();
    }

    [API.APIDescription("Lists the cloud providers this extension supports, with their settings prefixes and (for instance-renting providers) their start/stop/status route names - so a UI can build its provider table from the server instead of hardcoding it.",
        """
            "providers":
            [
                {
                    "prefix": "RunPodPods_",
                    "label": "RunPod GPU Pods",
                    "key_type": "runpod_api",
                    "is_instance": true,
                    "item_label": "Pod", // only for instance providers
                    "start_call": "CloudStartPod",
                    "start_call_ws": "CloudStartPodWS",
                    "stop_call": "CloudStopPod",
                    "stop_call_ws": "CloudStopPodWS",
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
                ["is_instance"] = def.IsInstance
            };
            if (def.Prefix == "RunPodPods_")
            {
                entry["item_label"] = "Pod";
                entry["start_call"] = "CloudStartPod";
                entry["start_call_ws"] = "CloudStartPodWS";
                entry["stop_call"] = "CloudStopPod";
                entry["stop_call_ws"] = "CloudStopPodWS";
                entry["status_call"] = "CloudGetPodStatus";
            }
            else if (def.Prefix == "VastAIInstance_")
            {
                entry["item_label"] = "Instance";
                entry["start_call"] = "VastAIStartInstance";
                entry["start_call_ws"] = "VastAIStartInstanceWS";
                entry["stop_call"] = "VastAIStopInstance";
                entry["stop_call_ws"] = "VastAIStopInstanceWS";
                entry["status_call"] = "VastAIGetInstanceStatus";
            }
            providers.Add(entry);
        }
        return Task.FromResult(new JObject { ["providers"] = providers });
    }

    [API.APIDescription("Debug/testing hook: corrupts the cached remote worker session ID for a serverless cloud backend, so the session-recovery path can be exercised deterministically (remote sessions otherwise last ~31 days). Admin-only; no effect on the worker itself.",
        """
            "message": "Worker session invalidated for backend #1."
        """)]
    public static Task<JObject> CloudDebugInvalidateSession(Session session,
        [API.APIParameter("ID of the serverless cloud backend whose cached worker session to invalidate.")] int backend_id)
    {
        CloudBackendBase backend = Program.Backends.RunningBackendsOfType<CloudBackendBase>()
            .FirstOrDefault(b => b.BackendData?.ID == backend_id);
        if (backend is null)
        {
            return Task.FromResult(new JObject { ["error"] = $"No running serverless cloud backend found with ID {backend_id}." });
        }
        if (backend.CurrentWorker is null)
        {
            return Task.FromResult(new JObject { ["error"] = "Backend has no active worker session to invalidate." });
        }
        backend.CurrentWorker.SessionId = "swarm_debug_invalidated_session";
        Logs.Info($"[CloudBackends] Debug: invalidated worker session for backend #{backend_id}.");
        return Task.FromResult(new JObject { ["message"] = $"Worker session invalidated for backend #{backend_id}." });
    }
}
