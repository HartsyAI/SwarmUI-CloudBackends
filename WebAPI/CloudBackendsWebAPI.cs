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
        API.RegisterAPICall(RefreshModels, true, CloudBackendsExtension.PermUseRunPodServerless);
        API.RegisterAPICall(GetStatus, false, CloudBackendsExtension.PermUseRunPodServerless);
        API.RegisterAPICall(StopPod, true, CloudBackendsExtension.PermUseRunPodPods);
        Logs.Verbose("[CloudBackendsWebAPI] Registered API routes: RefreshModels, GetStatus, StopPod");
    }

    /// <summary>Manually trigger a model refresh from workers for all running cloud backends.</summary>
    public static async Task<JObject> RefreshModels(Session session)
    {
        try
        {
            CloudBackendBase[] backends = [.. Program.Backends.RunningBackendsOfType<CloudBackendBase>()];
            if (backends.Length is 0)
                return new JObject { ["success"] = false, ["error"] = "No cloud backends are currently running." };
            int refreshed = 0, failed = 0;
            foreach (CloudBackendBase backend in backends)
            {
                try
                {
                    Logs.Debug($"[CloudBackends] Refreshing models for backend #{backend.BackendData?.ID} ({backend.Provider?.ProviderName})...");
                    await backend.RefreshModelsFromWorkerAsync(session);
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
            Logs.Error($"[CloudBackends] Error in RefreshModels: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Get status for all running cloud backends across all providers.</summary>
    public static async Task<JObject> GetStatus(Session session)
    {
        try
        {
            JArray statuses = [];
            foreach (CloudBackendBase backend in Program.Backends.RunningBackendsOfType<CloudBackendBase>())
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
            return new JObject { ["success"] = true, ["backends"] = statuses, ["total"] = statuses.Count };
        }
        catch (Exception ex)
        {
            Logs.Error($"[CloudBackends] Error in GetStatus: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Stop a RunPod GPU pod by backend ID.
    /// The pod remains stopped until the next generation request resumes it.
    /// </summary>
    public static async Task<JObject> StopPod(Session session, string backend_id)
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
            Logs.Error($"[CloudBackends] Error in StopPod: {ex.ReadableString()}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
