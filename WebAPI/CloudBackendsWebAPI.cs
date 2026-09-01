using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.CloudBackends.WebAPI;

/// <summary>Unified Web API routes for all cloud backend providers.</summary>
public static class CloudBackendsWebAPI
{
    public static void Register()
    {
        API.RegisterAPICall(CloudRefreshModels, true, CloudBackendsExtension.PermCloudStatus);
        API.RegisterAPICall(CloudGetStatus, false, CloudBackendsExtension.PermCloudStatus);
        API.RegisterAPICall(CloudStartPod, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudStopPod, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudGetPodStatus, false, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudDebugInvalidateSession, true, Permissions.EditBackends);
        API.RegisterAPICall(CloudListRunPodOptions, false, Permissions.EditBackends);
        Logs.Verbose("[CloudBackendsWebAPI] Registered API routes: CloudRefreshModels, CloudGetStatus, CloudStartPod, CloudStopPod, CloudGetPodStatus, CloudDebugInvalidateSession, CloudListRunPodOptions");
    }

    /// <summary>True if the session's user may act on this backend (each provider defines its own permission).</summary>
    static bool HasBackendPermission(CloudBackendBase backend, Session session)
    {
        // Only a permission refusal means "not yours" - anything else is a real fault and must not be
        // silently reported to the user as "no cloud backends are running".
        try { backend.CheckPermission(session); return true; }
        catch (SwarmReadableErrorException) { return false; }
    }

    /// <summary>
    /// Finds the RunPod GPU Pods backend for a given ID. The frontend only ever sees the parent "Cloud
    /// Backends" backend's ID, never the hidden child's own (negative, nonreal) ID, so this matches
    /// either: the child's own ID directly (in case a caller ever has it), or - the normal case - a
    /// running RunPodPodsBackend whose <see cref="BackendHandler.BackendData.AbstractParent"/> is the
    /// given parent ID (set in <see cref="CloudBackendsBackend.AddChild"/>).
    /// </summary>
    static RunPodPodsBackend FindRunPodPodsChild(int id) =>
        Program.Backends.RunningBackendsOfType<RunPodPodsBackend>()
            .FirstOrDefault(b => b.BackendData?.ID == id || b.BackendData?.AbstractParent?.ID == id);

    /// <summary>
    /// A "not found" message for RunPod Pods actions. If the child exists but isn't RUNNING (most likely
    /// ERRORED, e.g. a missing API key or bad config), that is a much more useful thing to report than a
    /// bare "not found" - so look past the RUNNING filter for one last diagnostic attempt.
    /// </summary>
    static string RunPodPodsNotFoundMessage(int id)
    {
        AbstractBackend match = Program.Backends.AllBackends.Values
            .Select(d => d.AbstractBackend)
            .FirstOrDefault(b => b is RunPodPodsBackend && (b.AbstractBackendData?.ID == id || b.AbstractBackendData?.AbstractParent?.ID == id));
        if (match is not null)
        {
            string lastStatus = match.LoadStatusReport?.LastOrDefault()?.Message;
            return $"RunPod GPU Pods backend for #{id} is {match.Status} (not usable right now)."
                + (string.IsNullOrWhiteSpace(lastStatus) ? "" : $" Last status: {lastStatus}");
        }
        return $"No RunPod GPU Pods backend found for #{id}. Is the RunPod GPU Pods section enabled and saved?";
    }

    /// <summary>Manually trigger a model refresh from workers for all running cloud backends the user may access.</summary>
    public static async Task<JObject> CloudRefreshModels(Session session)
    {
        try
        {
            CloudBackendBase[] backends = [.. Program.Backends.RunningBackendsOfType<CloudBackendBase>().Where(b => HasBackendPermission(b, session))];
            if (backends.Length is 0)
                return new JObject { ["success"] = false, ["error"] = "No cloud backends are currently running (that you have permission for)." };
            int refreshed = 0, failed = 0;
            JArray errors = [];
            foreach (CloudBackendBase backend in backends)
            {
                try
                {
                    Logs.Debug($"[CloudBackends] Refreshing models for backend #{backend.BackendData?.ID} ({backend.Provider?.ProviderName})...");
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
                ["success"] = true,
                ["refreshed"] = refreshed,
                ["failed"] = failed,
                ["errors"] = errors,
                ["message"] = message
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudRefreshModels: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Get status for all running cloud backends the user may access, across all providers.</summary>
    public static Task<JObject> CloudGetStatus(Session session)
    {
        try
        {
            JArray statuses = [];
            foreach (CloudBackendBase backend in Program.Backends.RunningBackendsOfType<CloudBackendBase>().Where(b => HasBackendPermission(b, session)))
            {
                statuses.Add(new JObject
                {
                    ["id"] = backend.BackendData?.ID,
                    ["title"] = backend.Title,
                    ["provider"] = backend.Provider?.ProviderName ?? "Unknown",
                    ["status"] = backend.Status.ToString(),
                    ["endpoint"] = backend.BaseConfig.EndpointId,
                    ["model_count"] = backend.Models?.Values.Sum(l => l.Count) ?? 0,
                    ["worker_id"] = backend.CurrentWorker?.WorkerId,
                    ["worker_url"] = backend.CurrentWorker?.PublicUrl,
                    ["max_concurrent"] = backend.BaseConfig.MaxConcurrent,
                    ["auto_refresh"] = backend.BaseConfig.AutoRefresh
                });
            }
            return Task.FromResult(new JObject { ["success"] = true, ["backends"] = statuses, ["total"] = statuses.Count });
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudGetStatus: {ex.ReadableString()}");
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// Starts (or creates, per settings) the cloud instance behind a RunPod Pods backend and attaches
    /// its Swarm backend. <paramref name="backend_id"/> is the "Cloud Backends" parent's ID.
    /// </summary>
    public static async Task<JObject> CloudStartPod(Session session, string backend_id)
    {
        try
        {
            if (!int.TryParse(backend_id, out int id))
                return new JObject { ["success"] = false, ["error"] = "Invalid backend_id." };
            RunPodPodsBackend backend = FindRunPodPodsChild(id);
            if (backend is null)
                return new JObject { ["success"] = false, ["error"] = RunPodPodsNotFoundMessage(id) };
            backend.CheckPermission(session);
            await backend.StartInstanceAsync();
            return new JObject { ["success"] = true, ["message"] = $"Pod started for backend #{id}." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudStartPod: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Stops the cloud instance behind a RunPod Pods backend, detaching its Swarm backend first.
    /// The instance stays stopped until the backend is started again. <paramref name="backend_id"/> is
    /// the "Cloud Backends" parent's ID.
    /// </summary>
    public static async Task<JObject> CloudStopPod(Session session, string backend_id)
    {
        try
        {
            if (!int.TryParse(backend_id, out int id))
                return new JObject { ["success"] = false, ["error"] = "Invalid backend_id." };
            RunPodPodsBackend backend = FindRunPodPodsChild(id);
            if (backend is null)
                return new JObject { ["success"] = false, ["error"] = RunPodPodsNotFoundMessage(id) };
            backend.CheckPermission(session);
            await backend.StopInstanceAsync();
            return new JObject { ["success"] = true, ["message"] = $"Pod stopped for backend #{id}." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudStopPod: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Live status of the pod behind a RunPod Pods backend - state, GPU, cost/hr, uptime, and which
    /// actions RunPod currently allows on it. Cached briefly server-side (see
    /// <see cref="RunPodPodsProvider.GetStatusAsync"/>) so a UI poll does not hammer RunPod's API.
    /// <paramref name="backend_id"/> is the "Cloud Backends" parent's ID.
    /// </summary>
    public static async Task<JObject> CloudGetPodStatus(Session session, string backend_id, bool force_refresh = false)
    {
        try
        {
            if (!int.TryParse(backend_id, out int id))
                return new JObject { ["success"] = false, ["error"] = "Invalid backend_id." };
            RunPodPodsBackend backend = FindRunPodPodsChild(id);
            if (backend is null)
                return new JObject { ["success"] = false, ["error"] = RunPodPodsNotFoundMessage(id) };
            backend.CheckPermission(session);
            if (backend.PodsProvider is null)
                return new JObject { ["success"] = false, ["error"] = "Backend has no active provider (not yet initialized)." };
            RunPodPodStatus status = await backend.PodsProvider.GetStatusAsync(force_refresh);
            if (status is null)
                return new JObject { ["success"] = true, ["has_pod"] = false, ["message"] = "No pod resolved yet - it is created on first Start." };
            return new JObject
            {
                ["success"] = true,
                ["has_pod"] = true,
                ["pod_id"] = status.PodId,
                ["status"] = status.Status,
                ["gpu_id"] = status.GpuId,
                ["gpu_count"] = status.GpuCount,
                ["cost_per_hour"] = status.CostPerHour,
                ["uptime_seconds"] = status.UptimeSeconds,
                ["allowed_actions"] = JArray.FromObject(status.AllowedActions),
                ["public_url"] = status.PublicUrl
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudGetPodStatus: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Lists what the user's RunPod account can actually use right now: GPU types with live
    /// availability and price, network volumes, data centers and templates.
    ///
    /// This exists so the backend settings form can offer real choices instead of asking someone to
    /// type an exact GPU name and hope it is spelled right and in stock.
    /// </summary>
    public static async Task<JObject> CloudListRunPodOptions(Session session, string cloud_type = "SECURE")
    {
        try
        {
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user session." };
            }
            string key = session.User.GetGenericData("runpod_api", "key")?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return new JObject { ["success"] = false, ["error"] = "No RunPod API key set. Add one in User Settings, API Keys, RunPod, then reopen this form." };
            }
            string cloud = string.Equals(cloud_type, "COMMUNITY", StringComparison.OrdinalIgnoreCase) ? "COMMUNITY" : "SECURE";
            using RunPodPodsProvider provider = new(key, new RunPodPodPlan { CloudType = cloud });
            return await provider.ListAccountOptionsAsync(cloud);
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudListRunPodOptions: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Debug/testing hook: corrupts the cached remote worker session ID for a cloud backend,
    /// so the session-recovery path can be exercised deterministically (remote sessions
    /// otherwise last ~31 days). Admin-only; no effect on the worker itself.
    /// </summary>
    public static Task<JObject> CloudDebugInvalidateSession(Session session, string backend_id)
    {
        if (!int.TryParse(backend_id, out int id))
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Invalid backend_id." });
        CloudBackendBase backend = Program.Backends.RunningBackendsOfType<CloudBackendBase>()
            .FirstOrDefault(b => b.BackendData?.ID == id);
        if (backend is null)
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = $"No running cloud backend found with ID {id}." });
        if (backend.CurrentWorker is null)
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Backend has no active worker session to invalidate." });
        backend.CurrentWorker.SessionId = "swarm_debug_invalidated_session";
        Logs.Info($"[CloudBackends] Debug: invalidated worker session for backend #{id}.");
        return Task.FromResult(new JObject { ["success"] = true, ["message"] = $"Worker session invalidated for backend #{id}." });
    }
}
