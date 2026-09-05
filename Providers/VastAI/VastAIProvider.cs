using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>
/// <see cref="ICloudProvider"/> for Vast.ai serverless endpoints.
///
/// Vast routing is a control plane only: POST https://run.vast.ai/route/ asks the serverless engine for a
/// worker and returns a signed grant, after which the client talks straight to the worker's IP and port.
/// There is no proxy or tunnel in the data path.
///
/// Requests to a worker carry the envelope { auth_data, session_id, payload }. The grant returned by
/// /route/ is forwarded as auth_data verbatim, because its signature covers those exact fields.
///
/// The route the worker exposes and the shape of payload are defined by the worker image, not by Vast.
/// This provider targets the handler in workers/vastai/vast_handler.py, which serves the configured
/// route and answers an { action } payload with { public_url, session_id, worker_id, version }.
/// </summary>
public class VastAIProvider(string apiKey, string endpointName, string workerRoute = "handler") : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();
    const string RouteBase = "https://run.vast.ai";
    const string ConsoleBase = "https://console.vast.ai";

    /// <summary>
    /// Per-endpoint key used for /route/ calls. Vast issues one key per serverless endpoint, which is
    /// what the routing engine expects; the account key is only used to look it up.
    /// </summary>
    string _endpointApiKey;

    public string ProviderName => "Vast.ai";
    public string ApiKeyType => "vastai_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            throw new SwarmReadableErrorException("No Vast.ai endpoint name is set. Set 'EndpointId' in the backend settings to your serverless endpoint's name.");
        }
        // Proves the account key works, and gives a clean 401 message if it does not.
        await ConsoleApiAsync(HttpMethod.Get, "/api/v0/users/current/", null, cancel);
        await ResolveEndpointKeyAsync(cancel);
    }

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        await ResolveEndpointKeyAsync(cancel);
        Logs.Info($"[VastAI] Requesting a worker for endpoint '{endpointName}' (max {maxWaitSeconds}s)...");
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        // request_idx holds our place in the engine's queue. Dropping it on a retry sends us to the back.
        int requestIdx = 0;
        string lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            JObject route = await RouteAsync(requestIdx, cancel);
            requestIdx = route["request_idx"]?.Value<int>() ?? requestIdx;
            string workerUrl = route["url"]?.ToString();
            // Readiness is the presence of a url, not any status field.
            if (!string.IsNullOrWhiteSpace(workerUrl))
            {
                Logs.Debug($"[VastAI] Routed to worker at {workerUrl}, sending wakeup...");
                JObject output = await SendToWorkerAsync(route, new JObject { ["action"] = "wakeup" }, cancel);
                if (output["success"]?.Value<bool>() is false)
                {
                    throw new SwarmReadableErrorException($"Vast.ai worker wakeup failed: {output["error"]}");
                }
                string publicUrl = output["public_url"]?.ToString();
                string sessionId = output["session_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(sessionId))
                {
                    throw new SwarmReadableErrorException($"Vast.ai worker answered but did not return public_url and session_id. Check that the worker image runs the SwarmUI handler. Output: {output}");
                }
                Logs.Info($"[VastAI] Worker ready: {output["worker_id"]} at {publicUrl}");
                return new CloudWorkerInfo
                {
                    PublicUrl = publicUrl,
                    SessionId = sessionId,
                    WorkerId = output["worker_id"]?.ToString(),
                    Version = output["version"]?.ToString()
                };
            }
            string status = route["status"]?.ToString() ?? "waiting";
            if (status != lastStatus)
            {
                Logs.Info($"[VastAI] No worker ready yet (engine status: {status}); waiting...");
                lastStatus = status;
            }
            await Task.Delay(Math.Clamp(pollIntervalMs, 1000, 15000), cancel);
        }
        throw new SwarmReadableErrorException($"No Vast.ai worker became available for endpoint '{endpointName}' within {maxWaitSeconds}s (last engine status: {lastStatus ?? "unknown"}). Check the endpoint's max workers and that its workergroup can find matching offers.");
    }

    /// <summary>
    /// Vast bills workers while the serverless engine keeps them up, and the engine scales on its own
    /// metrics. There is no keepalive job to submit, so this only holds the SwarmUI session open.
    /// </summary>
    public Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
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
                    await Http.PostJson($"{workerUrl.TrimEnd('/')}/API/GetNewSession", [], null, cancel);
                    Logs.Verbose($"[VastAI] Keepalive ping OK ({elapsed / 1000}s/{durationSeconds}s)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logs.Verbose($"[VastAI] Keepalive ping failed: {ex.Message}");
                }
            }
        }, cancel);
        return Task.FromResult(true);
    }

    public Task StopKeepaliveAsync() => Task.CompletedTask;

    public void Dispose() { }

    // ── Vast.ai serverless routing ────────────────────────────────────────────

    /// <summary>Asks the serverless engine for a worker. Returns the raw grant, which doubles as auth_data.</summary>
    public async Task<JObject> RouteAsync(int requestIdx, CancellationToken cancel = default, double cost = 100.0)
    {
        string key = _endpointApiKey ?? apiKey;
        JObject payload = new()
        {
            ["endpoint"] = endpointName,
            ["cost"] = cost,
            ["api_key"] = key,
            ["request_idx"] = requestIdx,
            // Matches the official vast-sdk client (vastai/serverless/client/endpoint.py Endpoint._route),
            // which always sends this alongside request_idx - the routing engine's own retry/replay window.
            ["replay_timeout"] = 60.0
        };
        // The official client also puts the key on the query string (on every serverless call, not just
        // this one - see _make_request in vastai/serverless/client/connection.py). Matched here even
        // though the header alone authenticates fine, since it costs nothing and removes any doubt.
        using HttpRequestMessage request = new(HttpMethod.Post, $"{RouteBase}/route/?api_key={Uri.EscapeDataString(key)}")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Logs.Debug($"[VastAI] POST /route/ (endpoint: {endpointName}, request_idx: {requestIdx})");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SwarmReadableErrorException("Vast.ai rejected the API key for this endpoint (401). Check your key in User Settings -> API Keys.");
        }
        // The engine asks callers to back off on these rather than treating them as fatal.
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            Logs.Verbose($"[VastAI] /route/ transient {(int)response.StatusCode}, will retry.");
            return [];
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"Vast.ai /route/ failed ({(int)response.StatusCode}): {text}");
        }
        return JObject.Parse(text);
    }

    /// <summary>
    /// Posts to the routed worker using Vast's envelope. The grant is forwarded verbatim: its signature
    /// covers those fields, so rebuilding it by hand risks invalidating it.
    /// </summary>
    public async Task<JObject> SendToWorkerAsync(JObject routeGrant, JObject payload, CancellationToken cancel = default)
    {
        string workerUrl = routeGrant["url"]?.ToString() ?? throw new SwarmReadableErrorException("Vast.ai route grant had no worker url.");
        string url = $"{workerUrl.TrimEnd('/')}/{workerRoute.TrimStart('/')}";
        JObject envelope = new()
        {
            ["auth_data"] = routeGrant,
            ["session_id"] = null,
            ["payload"] = payload
        };
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope.ToString(), Encoding.UTF8, "application/json")
        };
        Logs.Verbose($"[VastAI] POST {url}");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"Vast.ai worker call to '{url}' failed ({(int)response.StatusCode}): {text}");
        }
        try { return JObject.Parse(text); }
        catch (Exception)
        {
            throw new SwarmReadableErrorException($"Vast.ai worker at '{url}' returned a non-JSON response. Check that the worker route '{workerRoute}' is correct for your worker image.");
        }
    }

    // ── Vast.ai console API ───────────────────────────────────────────────────

    /// <summary>
    /// Looks up the per-endpoint API key that /route/ expects, by endpoint name. Cached after the
    /// first success. Falls back to the account key if the endpoint carries no key of its own.
    /// </summary>
    public async Task ResolveEndpointKeyAsync(CancellationToken cancel = default)
    {
        if (_endpointApiKey is not null) { return; }
        JToken result = await ConsoleApiAsync(HttpMethod.Get, "/api/v0/endptjobs/", null, cancel);
        JArray endpoints = result?["results"] as JArray ?? result as JArray ?? [];
        List<string> names = [];
        foreach (JToken t in endpoints)
        {
            if (t is not JObject ep) { continue; }
            string name = ep["endpoint_name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) { names.Add(name); }
            if (string.Equals(name, endpointName, StringComparison.OrdinalIgnoreCase))
            {
                _endpointApiKey = ep["api_key"]?.ToString();
                Logs.Debug($"[VastAI] Matched endpoint '{name}' (id {ep["id"]}), using its endpoint key: {(_endpointApiKey is null ? "none, falling back to account key" : "yes")}");
                _endpointApiKey ??= apiKey;
                return;
            }
        }
        throw new SwarmReadableErrorException($"Vast.ai has no serverless endpoint named '{endpointName}' on this account. Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.");
    }

    /// <summary>Calls the Vast.ai console REST API with the account key.</summary>
    public async Task<JToken> ConsoleApiAsync(HttpMethod method, string path, JObject body, CancellationToken cancel = default)
    {
        using HttpRequestMessage request = new(method, $"{ConsoleBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        }
        Logs.Debug($"[VastAI] {method} {path}");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SwarmReadableErrorException("Vast.ai API key was rejected (401). Check your key in User Settings -> API Keys.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"Vast.ai API {method} {path} failed ({(int)response.StatusCode}): {text}");
        }
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        try { return JToken.Parse(text); }
        catch (Exception) { return null; }
    }
}
