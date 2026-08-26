using Hartsy.Extensions.CloudBackends;
using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
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
        API.RegisterAPICall(CloudStopPod, true, CloudBackendsExtension.PermUseRunPodPods);
        API.RegisterAPICall(CloudDebugInvalidateSession, true, Permissions.EditBackends);
        Logs.Verbose("[CloudBackendsWebAPI] Registered API routes: CloudRefreshModels, CloudGetStatus, CloudStopPod, CloudDebugInvalidateSession");
    }

    /// <summary>True if the session's user may act on this backend (each provider defines its own permission).</summary>
    static bool HasBackendPermission(CloudBackendBase backend, Session session)
    {
        // Only a permission refusal means "not yours" - anything else is a real fault and must not be
        // silently reported to the user as "no cloud backends are running".
        try { backend.CheckPermission(session); return true; }
        catch (SwarmReadableErrorException) { return false; }
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
                }
            }
            return new JObject
            {
                ["success"] = true,
                ["refreshed"] = refreshed,
                ["failed"] = failed,
                ["message"] = $"Refreshed {refreshed} cloud backend(s), {failed} failed."
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
    /// Stop a RunPod GPU pod by backend ID.
    /// The pod remains stopped until the next generation request resumes it.
    /// </summary>
    public static async Task<JObject> CloudStopPod(Session session, string backend_id)
    {
        try
        {
            if (!int.TryParse(backend_id, out int id))
                return new JObject { ["success"] = false, ["error"] = "Invalid backend_id." };
            RunPodPodsBackend backend = Program.Backends.RunningBackendsOfType<RunPodPodsBackend>()
                .FirstOrDefault(b => b.BackendData?.ID == id);
            if (backend is null)
                return new JObject { ["success"] = false, ["error"] = $"No RunPod Pods backend found with ID {id}." };
            if (backend.Provider is not RunPodPodsProvider podsProvider)
                return new JObject { ["success"] = false, ["error"] = "Backend provider is not a RunPodPodsProvider." };
            await podsProvider.StopPodAsync();
            await backend.ClearWorkerStateAsync();
            return new JObject { ["success"] = true, ["message"] = $"Pod stop requested for backend #{id}." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in CloudStopPod: {ex.ReadableString()}");
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
