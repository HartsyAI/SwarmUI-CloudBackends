using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// <see cref="ICloudProvider"/> for RunPod Serverless endpoints.
///
/// Wakeup: POST /run with action=wakeup, poll GET /status/{jobId} until COMPLETED.
///         The custom handler returns {public_url, session_id, worker_id} in the job output.
/// Keepalive: Submit a separate long-running keepalive job via /run; cancel it on shutdown.
/// </summary>
public class RunPodServerlessProvider(string apiKey, string endpointId) : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _keepaliveJobIds = new();

    public string ProviderName => "RunPod Serverless";
    public string ApiKeyType => "runpod_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        string jobId = await SubmitJobAsync(new JObject { ["action"] = "wakeup" }, cancel);
        Logs.Info($"[RunPodServerless] Wakeup job {jobId} submitted. Waiting for worker (max {maxWaitSeconds}s)...");
        JObject output;
        try
        {
            output = await WaitForJobAsync(jobId, Math.Clamp(pollIntervalMs, 1000, 10000), maxWaitSeconds, cancel);
        }
        catch (Exception)
        {
            // Don't leave the wakeup job queued: it would wake (and bill) a worker later with nobody listening.
            await CancelJobAsync(jobId);
            throw;
        }
        if (output["success"]?.Value<bool>() is false)
            throw new SwarmReadableErrorException($"Worker wakeup failed: {output["error"]}");
        string publicUrl = output["public_url"]?.ToString();
        string sessionId = output["session_id"]?.ToString();
        if (string.IsNullOrEmpty(publicUrl) || string.IsNullOrEmpty(sessionId))
            throw new SwarmReadableErrorException($"Wakeup job completed but did not return public_url/session_id. Output: {output}");
        Logs.Info($"[RunPodServerless] Worker ready: {output["worker_id"]} at {publicUrl}");
        return new CloudWorkerInfo
        {
            PublicUrl = publicUrl,
            SessionId = sessionId,
            WorkerId = output["worker_id"]?.ToString(),
            Version = output["version"]?.ToString()
        };
    }

    /// <summary>
    /// Submits an additional keepalive job. Keepalive jobs are blocking and run one at a time, so a
    /// newly submitted job queues behind the running one and seamlessly extends the worker's life.
    ///
    /// Deliberately does NOT cancel the currently running keepalive: RunPod terminates the worker that
    /// is executing a cancelled job, so "cancel old, submit new" kills the worker mid-session (observed
    /// live: worker died ~13s after such a cancel, and every later call to its proxy URL returned empty).
    /// Outstanding jobs are cancelled only by <see cref="StopKeepaliveAsync"/>, where ending the worker
    /// is the desired outcome.
    /// </summary>
    public async Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
    {
        try
        {
            string jobId = await SubmitJobAsync(new JObject { ["action"] = "keepalive", ["duration"] = durationSeconds, ["interval"] = 30 }, cancel);
            _keepaliveJobIds[jobId] = 0;
            Logs.Debug($"[RunPodServerless] Keepalive job submitted: {jobId} (duration: {durationSeconds}s, outstanding: {_keepaliveJobIds.Count})");
            return true;
        }
        catch (Exception ex) { Logs.Warning($"[RunPodServerless] Failed to submit keepalive job (worker may scale down early): {ex.Message}"); return false; }
    }

    public async Task StopKeepaliveAsync()
    {
        foreach (string jobId in _keepaliveJobIds.Keys.ToArray())
        {
            if (_keepaliveJobIds.TryRemove(jobId, out _))
            {
                Logs.Debug($"[RunPodServerless] Cancelling keepalive job {jobId}...");
                await CancelJobAsync(jobId);
            }
        }
    }

    public void Dispose()
    {
        // Backend Shutdown() already awaited StopKeepaliveAsync; this is a best-effort backstop.
        // Fire-and-forget instead of sync-over-async to avoid deadlock risk.
        foreach (string jobId in _keepaliveJobIds.Keys.ToArray())
        {
            if (_keepaliveJobIds.TryRemove(jobId, out _)) { _ = CancelJobAsync(jobId); }
        }
    }

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new SwarmReadableErrorException("No RunPod serverless endpoint ID is set. Set 'EndpointId' in the backend settings.");
        }
        JObject health = await GetHealthAsync(cancel);
        Logs.Debug($"[RunPodServerless] Endpoint {endpointId} health: workers={health["workers"]?.ToString(Newtonsoft.Json.Formatting.None)}, jobs={health["jobs"]?.ToString(Newtonsoft.Json.Formatting.None)}");
    }

    // ── RunPod REST API helpers ───────────────────────────────────────────────

    /// <summary>Submit an async job to the RunPod endpoint. Returns job ID immediately.</summary>
    public async Task<string> SubmitJobAsync(JObject input, CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/run";
        JObject payload = new() { ["input"] = input };
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Logs.Debug($"[RunPodServerless] POST /run (action: {input["action"]})");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"RunPod /run failed ({response.StatusCode}): {error}");
        }
        JObject result = JObject.Parse(await response.Content.ReadAsStringAsync(cancel));
        string jobId = result["id"]?.ToString();
        if (string.IsNullOrEmpty(jobId)) throw new Exception($"RunPod /run did not return a job ID. Response: {result}");
        Logs.Debug($"[RunPodServerless] Job submitted: {jobId}");
        return jobId;
    }

    /// <summary>Poll GET /status/{jobId} until the job is COMPLETED or FAILED.</summary>
    public async Task<JObject> WaitForJobAsync(string jobId, int pollIntervalMs, int timeoutSec, CancellationToken cancel = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            string url = $"https://api.runpod.ai/v2/{endpointId}/status/{jobId}";
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using HttpResponseMessage res = await Http.SendAsync(req, cancel);
            JObject result = JObject.Parse(await res.Content.ReadAsStringAsync(cancel));
            string status = result["status"]?.ToString() ?? "UNKNOWN";
            if (status != lastStatus)
            {
                int elapsed = (int)(DateTime.UtcNow - deadline.AddSeconds(-timeoutSec)).TotalSeconds;
                Logs.Info($"[RunPodServerless] Job {jobId}: {status} (after {elapsed}s)");
                lastStatus = status;
            }
            if (status is "COMPLETED") return result["output"] as JObject ?? new JObject();
            if (status is "FAILED") throw new SwarmReadableErrorException($"RunPod job {jobId} failed: {result["error"]}");
            if (status is "CANCELLED" or "TIMED_OUT") throw new SwarmReadableErrorException($"RunPod job {jobId} ended without completing: {status}");
            await Task.Delay(pollIntervalMs, cancel);
        }
        throw new TimeoutException($"RunPod job {jobId} did not complete within {timeoutSec}s");
    }

    /// <summary>GET /health for the endpoint. Throws readable errors on bad key (401) or unknown endpoint (404).</summary>
    public async Task<JObject> GetHealthAsync(CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/health";
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage res = await Http.SendAsync(req, cancel);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new SwarmReadableErrorException("RunPod API key was rejected (401). Check your key in User Settings -> API Keys.");
        if (res.StatusCode is System.Net.HttpStatusCode.NotFound)
            throw new SwarmReadableErrorException($"RunPod endpoint '{endpointId}' not found (404). Check the EndpointId backend setting.");
        if (!res.IsSuccessStatusCode)
            throw new SwarmReadableErrorException($"RunPod /health for endpoint '{endpointId}' failed: {res.StatusCode}");
        return JObject.Parse(await res.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>Cancel a queued or running job. Best-effort - does not throw.</summary>
    public async Task CancelJobAsync(string jobId, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        try
        {
            string url = $"https://api.runpod.ai/v2/{endpointId}/cancel/{jobId}";
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            await Http.SendAsync(req, cancel);
            Logs.Debug($"[RunPodServerless] Cancelled job {jobId}");
        }
        catch (Exception ex) { Logs.Verbose($"[RunPodServerless] Cancel failed for {jobId}: {ex.Message}"); }
    }
}
