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

    string _keepaliveJobId = null;

    public string ProviderName => "RunPod Serverless";
    public string ApiKeyType => "runpod_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        string jobId = await SubmitJobAsync(new JObject { ["action"] = "wakeup" }, cancel);
        Logs.Info($"[RunPodServerless] Wakeup job {jobId} submitted. Waiting for worker (max {maxWaitSeconds}s)...");
        JObject output = await WaitForJobAsync(jobId, Math.Clamp(pollIntervalMs, 1000, 10000), maxWaitSeconds, cancel);
        string publicUrl = output["public_url"]?.ToString();
        string sessionId = output["session_id"]?.ToString();
        if (string.IsNullOrEmpty(publicUrl) || string.IsNullOrEmpty(sessionId))
            throw new Exception($"Wakeup job completed but did not return public_url/session_id. Output: {output}");
        Logs.Info($"[RunPodServerless] Worker ready: {output["worker_id"]} at {publicUrl}");
        return new CloudWorkerInfo
        {
            PublicUrl = publicUrl,
            SessionId = sessionId,
            WorkerId = output["worker_id"]?.ToString(),
            Version = output["version"]?.ToString()
        };
    }

    public async Task StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
    {
        try
        {
            string jobId = await SubmitJobAsync(new JObject { ["action"] = "keepalive", ["duration"] = durationSeconds, ["interval"] = 30 }, cancel);
            _keepaliveJobId = jobId;
            Logs.Debug($"[RunPodServerless] Keepalive job submitted: {jobId} (duration: {durationSeconds}s)");
        }
        catch (Exception ex) { Logs.Warning($"[RunPodServerless] Failed to submit keepalive job (worker may scale down early): {ex.Message}"); }
    }

    public async Task StopKeepaliveAsync()
    {
        if (!string.IsNullOrEmpty(_keepaliveJobId))
        {
            Logs.Debug($"[RunPodServerless] Cancelling keepalive job {_keepaliveJobId}...");
            await CancelJobAsync(_keepaliveJobId);
            _keepaliveJobId = null;
        }
    }

    public void Dispose()
    {
        StopKeepaliveAsync().GetAwaiter().GetResult();
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
            if (status is "FAILED") throw new Exception($"RunPod job {jobId} failed: {result["error"]}");
            await Task.Delay(pollIntervalMs, cancel);
        }
        throw new TimeoutException($"RunPod job {jobId} did not complete within {timeoutSec}s");
    }

    /// <summary>Cancel a queued or running job. Best-effort — does not throw.</summary>
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
