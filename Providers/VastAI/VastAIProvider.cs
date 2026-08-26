using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// <see cref="ICloudProvider"/> for Vast.ai serverless routing.
///
/// Wakeup: POST /route/ to discover an available worker URL + auth signature,
///         then POST to the worker with an auth_data envelope to trigger the wakeup handler.
///         The handler returns {public_url, session_id, worker_id}.
///
/// Keepalive: Background task that pings the worker's /API/GetNewSession every 30 seconds
///            to prevent SwarmUI session expiry. Cancelled via CancellationToken.
/// </summary>
public class VastAIProvider(string apiKey, string endpointName) : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    public string ProviderName => "Vast.ai";
    public string ApiKeyType => "vastai_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        Logs.Info($"[VastAI] Waking worker for endpoint '{endpointName}' (max {maxWaitSeconds}s)...");
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            RouteResponse route = await RouteToWorkerAsync(cancel);
            if (!string.IsNullOrEmpty(route.Url))
            {
                Logs.Debug($"[VastAI] Worker available at {route.Url}, sending wakeup...");
                JObject output = await SendToWorkerAsync(route, "handler", new JObject { ["action"] = "wakeup" }, cancel);
                string publicUrl = output["public_url"]?.ToString();
                string sessionId = output["session_id"]?.ToString();
                if (string.IsNullOrEmpty(publicUrl) || string.IsNullOrEmpty(sessionId))
                    throw new Exception($"Wakeup returned incomplete data. Output: {output}");
                Logs.Info($"[VastAI] Worker ready: {output["worker_id"]} at {publicUrl}");
                return new CloudWorkerInfo
                {
                    PublicUrl = publicUrl,
                    SessionId = sessionId,
                    WorkerId = output["worker_id"]?.ToString(),
                    Version = output["version"]?.ToString()
                };
            }
            string status = route.Status ?? "UNKNOWN";
            if (status != lastStatus)
            {
                int elapsed = (int)(DateTime.UtcNow - deadline.AddSeconds(-maxWaitSeconds)).TotalSeconds;
                Logs.Info($"[VastAI] Waiting for worker: {status} (after {elapsed}s)");
                lastStatus = status;
            }
            await Task.Delay(pollIntervalMs, cancel);
        }
        throw new TimeoutException($"No Vast.ai worker available for '{endpointName}' within {maxWaitSeconds}s");
    }

    public Task StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
    {
        string workerUrl = worker.PublicUrl;
        _ = Task.Run(async () =>
        {
            int intervalMs = 30_000;
            int elapsed = 0;
            while (elapsed < durationSeconds * 1000 && !cancel.IsCancellationRequested)
            {
                try { await Task.Delay(intervalMs, cancel); }
                catch (OperationCanceledException) { break; }
                elapsed += intervalMs;
                try
                {
                    await Http.PostJson($"{workerUrl}/API/GetNewSession", new JObject());
                    Logs.Verbose($"[VastAI] Keepalive ping OK ({elapsed / 1000}s/{durationSeconds}s)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logs.Verbose($"[VastAI] Keepalive ping failed: {ex.Message}");
                }
            }
        }, cancel);
        return Task.CompletedTask;
    }

    public Task StopKeepaliveAsync()
    {
        // Keepalive loop stops automatically when the CancellationToken (from KeepaliveCts) is cancelled.
        return Task.CompletedTask;
    }

    public void Dispose() { /* no unmanaged resources */ }

    // ── Vast.ai REST API helpers ──────────────────────────────────────────────

    public record RouteResponse(string Url, float Cost, long ReqNum, string Signature, int RequestIdx, string Status);

    /// <summary>POST /route/ to discover a worker. Returns a URL when a worker is available.</summary>
    public async Task<RouteResponse> RouteToWorkerAsync(CancellationToken cancel = default, float cost = 100f)
    {
        JObject payload = new() { ["endpoint"] = endpointName, ["cost"] = cost };
        using HttpRequestMessage request = new(HttpMethod.Post, "https://run.vast.ai/route/")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Logs.Debug($"[VastAI] POST /route/ (endpoint: {endpointName})");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"Vast.ai /route/ failed ({response.StatusCode}): {error}");
        }
        JObject result = JObject.Parse(await response.Content.ReadAsStringAsync(cancel));
        string workerUrl = result["url"]?.ToString();
        if (string.IsNullOrEmpty(workerUrl))
            return new RouteResponse(null, cost, 0, null, 0, result["status"]?.ToString() ?? "no_workers");
        return new RouteResponse(
            Url: workerUrl,
            Cost: result["cost"]?.Value<float>() ?? cost,
            ReqNum: result["reqnum"]?.Value<long>() ?? 0,
            Signature: result["signature"]?.ToString(),
            RequestIdx: result["request_idx"]?.Value<int>() ?? 0,
            Status: null);
    }

    /// <summary>POST to a worker with the Vast.ai auth_data envelope (required during wakeup routing).</summary>
    public async Task<JObject> SendToWorkerAsync(RouteResponse route, string handlerRoute, JObject payload, CancellationToken cancel = default)
    {
        string url = $"{route.Url.TrimEnd('/')}/{handlerRoute.TrimStart('/')}";
        JObject envelope = new()
        {
            ["auth_data"] = new JObject
            {
                ["signature"] = route.Signature,
                ["cost"] = route.Cost,
                ["endpoint"] = endpointName,
                ["reqnum"] = route.ReqNum,
                ["url"] = route.Url,
                ["request_idx"] = route.RequestIdx
            },
            ["payload"] = payload
        };
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope.ToString(), Encoding.UTF8, "application/json")
        };
        Logs.Verbose($"[VastAI] POST {url} (auth_data envelope)");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"Vast.ai worker call failed ({response.StatusCode}): {error}");
        }
        return JObject.Parse(await response.Content.ReadAsStringAsync(cancel));
    }
}
