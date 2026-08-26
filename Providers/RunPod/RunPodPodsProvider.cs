using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// <see cref="ICloudProvider"/> for RunPod on-demand GPU pods.
///
/// Wakeup flow:
///   1. Query pod by ID via RunPod GraphQL API.
///   2. If the pod is stopped, resume it (mutation podResume).
///   3. Poll until the pod runtime shows the container is up.
///   4. Build the public URL from RunPod's proxy format: https://{podId}-{port}.proxy.runpod.net
///   5. Call /API/GetNewSession on the running SwarmUI instance to obtain a session ID.
///
/// Keepalive: background task that pings /API/GetNewSession every 5 minutes
///            (pods stay running on their own; this only refreshes the SwarmUI session).
///
/// The pod is NOT stopped on shutdown — it keeps running so the user can reuse it.
/// Use the StopPod WebAPI call or the RunPod dashboard to stop it manually.
/// </summary>
public class RunPodPodsProvider(string apiKey, string podId, int swarmPort) : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();
    const string GraphQLUrl = "https://api.runpod.io/graphql";

    public string ProviderName => "RunPod GPU Pods";
    public string ApiKeyType => "runpod_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        Logs.Info($"[RunPodPods] Waking pod '{podId}' (max {maxWaitSeconds}s, port {swarmPort})...");

        // 1. Query current pod state.
        JObject podData = await QueryPodAsync(cancel);
        string desiredStatus = podData["desiredStatus"]?.ToString() ?? "";
        Logs.Debug($"[RunPodPods] Pod '{podId}' desiredStatus: {desiredStatus}");

        // 2. Resume if stopped.
        if (!desiredStatus.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            Logs.Info($"[RunPodPods] Resuming pod '{podId}'...");
            await ResumePodAsync(cancel);
        }

        // 3. Poll until the container reports uptime > 0.
        string publicUrl = $"https://{podId}-{swarmPort}.proxy.runpod.net";
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        int clampedPollMs = Math.Clamp(pollIntervalMs, 3000, 15000);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject pod = await QueryPodAsync(cancel);
                int uptimeSec = pod["runtime"]?["uptimeInSeconds"]?.Value<int>() ?? 0;
                string containerStatus = pod["runtime"]?["container"]?["cpuPercent"]?.ToString();
                Logs.Debug($"[RunPodPods] Pod '{podId}': uptime={uptimeSec}s");
                if (uptimeSec > 0)
                {
                    Logs.Info($"[RunPodPods] Pod '{podId}' is running. Connecting to SwarmUI at {publicUrl}...");
                    break;
                }
            }
            catch (Exception ex) { Logs.Verbose($"[RunPodPods] Pod status poll error: {ex.Message}"); }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException($"Pod '{podId}' did not start within {maxWaitSeconds}s");

        // 4. Create a SwarmUI session on the running pod.
        string sessionId = await CreateSwarmSessionAsync(publicUrl, cancel);
        Logs.Info($"[RunPodPods] Connected to SwarmUI on pod '{podId}' (session: {sessionId[..Math.Min(16, sessionId.Length)]}...)");

        return new CloudWorkerInfo
        {
            PublicUrl = publicUrl,
            SessionId = sessionId,
            WorkerId = podId,
            Version = null
        };
    }

    public Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
    {
        // Pods stay running on their own. We only ping SwarmUI to prevent session expiry.
        string workerUrl = worker.PublicUrl;
        string sessionId = worker.SessionId;
        _ = Task.Run(async () =>
        {
            int intervalMs = 5 * 60 * 1000; // 5 minutes
            int elapsed = 0;
            while (elapsed < durationSeconds * 1000 && !cancel.IsCancellationRequested)
            {
                try { await Task.Delay(intervalMs, cancel); }
                catch (OperationCanceledException) { break; }
                elapsed += intervalMs;
                try
                {
                    await Http.PostJson($"{workerUrl}/API/GetNewSession", new JObject());
                    Logs.Verbose($"[RunPodPods] Session keepalive OK ({elapsed / 1000}s/{durationSeconds}s)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logs.Verbose($"[RunPodPods] Session keepalive ping failed: {ex.Message}");
                }
            }
        }, cancel);
        // The pod is kept alive by a local ping loop, which is always started successfully.
        return Task.FromResult(true);
    }

    public Task StopKeepaliveAsync()
    {
        // Keepalive loop stops via CancellationToken. Pod intentionally left running.
        Logs.Debug($"[RunPodPods] StopKeepaliveAsync: pod '{podId}' will remain running.");
        return Task.CompletedTask;
    }

    public void Dispose() { /* no unmanaged resources */ }

    // ── RunPod GraphQL helpers ────────────────────────────────────────────────

    /// <summary>Query the pod's current state from RunPod GraphQL API.</summary>
    public async Task<JObject> QueryPodAsync(CancellationToken cancel = default)
    {
        string query = """
            query Pod($podId: String!) {
              pod(input: { podId: $podId }) {
                id
                name
                desiredStatus
                runtime {
                  uptimeInSeconds
                  container { cpuPercent }
                }
              }
            }
            """;
        JObject result = await GraphQLAsync(query, new JObject { ["podId"] = podId }, cancel);
        JObject pod = result["data"]?["pod"] as JObject;
        if (pod is null) throw new Exception($"Pod '{podId}' not found. Check your Pod ID in backend settings.");
        return pod;
    }

    /// <summary>Resume (start) a stopped pod.</summary>
    public async Task ResumePodAsync(CancellationToken cancel = default)
    {
        string mutation = """
            mutation PodResume($podId: String!) {
              podResume(input: { podId: $podId }) {
                id
                desiredStatus
              }
            }
            """;
        JObject result = await GraphQLAsync(mutation, new JObject { ["podId"] = podId }, cancel);
        Logs.Debug($"[RunPodPods] podResume response: {result["data"]?["podResume"]}");
    }

    /// <summary>Stop a running pod (exposed for manual use via WebAPI, not called automatically on backend shutdown).</summary>
    public async Task StopPodAsync(CancellationToken cancel = default)
    {
        string mutation = """
            mutation PodStop($podId: String!) {
              podStop(input: { podId: $podId }) {
                id
                desiredStatus
              }
            }
            """;
        JObject result = await GraphQLAsync(mutation, new JObject { ["podId"] = podId }, cancel);
        Logs.Info($"[RunPodPods] Pod '{podId}' stop requested. Response: {result["data"]?["podStop"]}");
    }

    /// <summary>Execute a GraphQL query or mutation against RunPod's API.</summary>
    public async Task<JObject> GraphQLAsync(string query, JObject variables, CancellationToken cancel = default)
    {
        JObject payload = new() { ["query"] = query, ["variables"] = variables };
        using HttpRequestMessage request = new(HttpMethod.Post, GraphQLUrl)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string content = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"RunPod GraphQL failed ({response.StatusCode}): {content}");
        JObject result = JObject.Parse(content);
        if (result["errors"] is JArray errors && errors.Count > 0)
            throw new Exception($"RunPod GraphQL errors: {errors[0]["message"]}");
        return result;
    }

    // ── SwarmUI session creation ──────────────────────────────────────────────

    /// <summary>
    /// Call /API/GetNewSession on the running SwarmUI instance to obtain a valid session ID.
    /// Retries for up to 2 minutes in case SwarmUI is still initialising after the pod started.
    /// </summary>
    async Task<string> CreateSwarmSessionAsync(string publicUrl, CancellationToken cancel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject result = await Http.PostJson($"{publicUrl}/API/GetNewSession", new JObject());
                string sessionId = result["session_id"]?.ToString();
                if (!string.IsNullOrEmpty(sessionId)) return sessionId;
            }
            catch (Exception ex) { Logs.Verbose($"[RunPodPods] Waiting for SwarmUI to start on pod: {ex.Message}"); }
            await Task.Delay(5000, cancel);
        }
        throw new TimeoutException($"SwarmUI on pod '{podId}' did not respond within 2 minutes of container start.");
    }
}
