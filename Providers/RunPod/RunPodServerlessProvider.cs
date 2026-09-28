using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>A RunPod API error, with the HTTP status so callers can tell a permanent failure from a transient one.</summary>
public class RunPodApiException(int status, string message) : SwarmReadableErrorException(message)
{
    /// <summary>HTTP status RunPod answered with.</summary>
    public readonly int Status = status;

    /// <summary>True for answers that will not change on retry: bad credentials, or an endpoint or job that no longer exists.</summary>
    public bool IsPermanent => Status is 401 or 403 or 404;
}

/// <summary>
/// <see cref="ICloudProvider"/> for RunPod Serverless (queue) endpoints running the Hartsy RunPod worker image
/// (github.com/HartsyAI/RunPod-Worker-SwarmUI, version 2 or later).
///
/// A lease is one async job, <c>{"action": "lease"}</c>, handled by a streaming (generator) handler. Its first
/// streamed output carries the worker's proxy URL and a per-lease gateway token; the job then keeps running,
/// holding its worker, until the worker has been idle and ends it. A running lease occupies its worker, so a
/// second queued lease is real queue pressure and RunPod's autoscaler adds a worker for it.
/// See https://docs.runpod.io/serverless/workers/handler-functions for the streaming contract.
/// </summary>
public class RunPodServerlessProvider(string apiKey, string endpointId) : ICloudProvider
{
    /// <summary>Shared client for RunPod's APIs.</summary>
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>The lease protocol this extension speaks. Workers older than this are refused.</summary>
    public const int RequiredProtocol = 2;

    /// <summary>Log prefix.</summary>
    const string Tag = "[RunPodServerless]";

    /// <inheritdoc/>
    public string ProviderName => "RunPod Serverless";

    /// <inheritdoc/>
    public string ApiKeyType => "runpod_api";

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    // ── Leases ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<CloudWorkerInfo> AcquireWorkerAsync(LeaseRequest request, CancellationToken cancel)
    {
        JObject input = new()
        {
            ["action"] = "lease",
            ["idle_seconds"] = request.IdleSeconds,
            ["startup_grace_seconds"] = request.StartupGraceSeconds,
            ["max_lease_seconds"] = request.MaxLeaseSeconds
        };
        string jobId = await SubmitJobAsync(input, CancellationToken.None);
        Logs.Info($"{Tag} Lease job {jobId} submitted for endpoint {endpointId}.");
        DateTime deadline = DateTime.UtcNow.AddSeconds(request.StartupTimeoutSec);
        int pollMs = Math.Clamp(request.PollIntervalMs, 1000, 5000);
        string lastStatus = null;
        bool withdrawn = false;
        using CancellationTokenSource waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, Program.GlobalProgramCancel);
        try
        {
            while (true)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new SwarmReadableErrorException($"No RunPod worker picked up the lease within {request.StartupTimeoutSec}s. Check the endpoint's max workers, GPU availability, and worker logs.");
                }
                JObject stream;
                try
                {
                    // Cancellable until a withdraw is requested; after that, polls run to completion so the job's
                    // status can decide between withdrawing (still queued) and keeping it (already assigned).
                    stream = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/stream/{jobId}", cancel.IsCancellationRequested ? CancellationToken.None : cancel);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested && !Program.GlobalProgramCancel.IsCancellationRequested)
                {
                    continue;
                }
                string status = stream["status"]?.ToString() ?? "UNKNOWN";
                if (status != lastStatus)
                {
                    Logs.Debug($"{Tag} Lease job {jobId}: {status}");
                    lastStatus = status;
                }
                JObject first = FirstOutput(stream);
                if (first is not null)
                {
                    return ToWorker(jobId, first);
                }
                if (status is "COMPLETED" or "FAILED" or "CANCELLED" or "TIMED_OUT")
                {
                    throw await DescribeEndedJobAsync(jobId, status);
                }
                if (cancel.IsCancellationRequested && status is "IN_QUEUE")
                {
                    // Nobody needs this worker anymore and none has been assigned: withdraw it before it bills.
                    // Only a confirmed cancel ends tracking; otherwise keep polling, and keep the worker if one is assigned.
                    if (await CancelJobAsync(jobId))
                    {
                        withdrawn = true;
                        Logs.Debug($"{Tag} Lease job {jobId} withdrawn while still queued.");
                        cancel.ThrowIfCancellationRequested();
                    }
                }
                // Once IN_PROGRESS a worker is already assigned and billing, so it is kept (see the interface docs).
                try
                {
                    await Task.Delay(pollMs, cancel.IsCancellationRequested ? Program.GlobalProgramCancel : waitCancel.Token);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested && !Program.GlobalProgramCancel.IsCancellationRequested)
                {
                    // Withdraw requested mid-wait: check the job's status right away.
                }
            }
        }
        catch (Exception) when (!withdrawn)
        {
            await CancelJobAsync(jobId);
            throw;
        }
    }

    /// <summary>The first streamed output, or null if none has arrived yet.</summary>
    internal static JObject FirstOutput(JObject stream)
    {
        if (stream["stream"] is JArray chunks && chunks.Count > 0)
        {
            return chunks[0]["output"] as JObject;
        }
        return null;
    }

    /// <summary>Validates a lease's first output and turns it into worker info.</summary>
    internal static CloudWorkerInfo ToWorker(string jobId, JObject output)
    {
        if (output["success"]?.Value<bool>() is not true)
        {
            throw new SwarmReadableErrorException($"RunPod worker refused the lease: {output["error"] ?? output}");
        }
        int protocol = output["protocol"]?.Value<int>() ?? 0;
        string publicUrl = output["public_url"]?.ToString();
        string token = output["token"]?.ToString();
        if (protocol < RequiredProtocol || string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(token))
        {
            throw new SwarmReadableErrorException($"The RunPod worker image is too old for this version of Cloud Backends. Point the endpoint at kalebbroo/swarmui-worker-runpod 2.0.0 or later.");
        }
        Logs.Info($"{Tag} Lease {jobId} holds worker {output["worker_id"]}.");
        return new CloudWorkerInfo
        {
            PublicUrl = publicUrl.TrimEnd('/'),
            Token = token,
            WorkerId = output["worker_id"]?.ToString(),
            LeaseId = jobId,
            Protocol = protocol
        };
    }

    /// <summary>Builds a readable error for a lease job that ended before streaming a worker.</summary>
    async Task<Exception> DescribeEndedJobAsync(string jobId, string status)
    {
        JObject job = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/status/{jobId}", CancellationToken.None);
        // Aggregated streaming jobs report output as an array of chunks; plain jobs as one object.
        JToken output = job["output"];
        string error = job["error"]?.ToString() ?? (output as JObject)?["error"]?.ToString() ?? ((output as JArray)?.FirstOrDefault() as JObject)?["error"]?.ToString();
        if (error is not null && error.Contains("Unknown action", StringComparison.OrdinalIgnoreCase))
        {
            return new SwarmReadableErrorException("The RunPod worker image is too old for this version of Cloud Backends. Point the endpoint at kalebbroo/swarmui-worker-runpod 2.0.0 or later.");
        }
        return new SwarmReadableErrorException($"RunPod lease job {jobId} ended ({status}) before a worker was ready{(error is null ? "." : $": {error}")}");
    }

    /// <inheritdoc/>
    public async Task<bool> IsLeaseActiveAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        try
        {
            JObject job = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/status/{worker.LeaseId}", cancel);
            return job["status"]?.ToString() is "IN_PROGRESS" or "IN_QUEUE";
        }
        catch (RunPodApiException ex) when (ex.IsPermanent)
        {
            // A revoked key or a deleted endpoint or job will never answer again: drop the lease, so the slot stops
            // counting toward Max Workers and the next lease rebuilds the provider with the owner's current key.
            Logs.Warning($"{Tag} Lease {worker.LeaseId} can no longer be checked ({ex.Status}: {ex.Message}); treating it as ended.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transient failure is not evidence the worker is gone. The next check, or the generations, will tell.
            Logs.Debug($"{Tag} Lease status check failed for {worker.LeaseId}: {ex.Message}");
            return true;
        }
    }

    /// <inheritdoc/>
    /// <remarks>The worker ends its own lease when idle, so there is nothing to renew.</remarks>
    public Task RenewLeaseAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>Cancelling a running RunPod job stops the worker running it, which is exactly what releasing means here.</remarks>
    public Task ReleaseLeaseAsync(CloudWorkerInfo worker)
    {
        return CancelJobAsync(worker.LeaseId);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new SwarmReadableErrorException("No RunPod serverless endpoint ID is set. Set 'EndpointId' in the backend settings.");
        }
        JObject health = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/health", cancel);
        Logs.Debug($"{Tag} Endpoint {endpointId} health: workers={health["workers"]?.ToString(Newtonsoft.Json.Formatting.None)}, jobs={health["jobs"]?.ToString(Newtonsoft.Json.Formatting.None)}");
    }

    /// <inheritdoc/>
    public async Task<JArray> CheckEndpointAsync(LeaseRequest request, int maxWorkers, CancellationToken cancel)
    {
        JArray findings = [];
        JObject endpoint = await GetJsonAsync($"https://rest.runpod.io/v1/endpoints/{endpointId}?includeTemplate=true", cancel);
        int workersMax = endpoint["workersMax"]?.Value<int>() ?? 0;
        long executionTimeoutMs = endpoint["executionTimeoutMs"]?.Value<long>() ?? 0;
        string image = endpoint["template"]?["imageName"]?.ToString() ?? "";
        if (executionTimeoutMs > 0 && executionTimeoutMs / 1000 <= request.MaxLeaseSeconds)
        {
            findings.Add(Finding("error", $"The endpoint's execution timeout ({executionTimeoutMs / 1000}s) must be longer than Max Lease Seconds ({request.MaxLeaseSeconds}s), or RunPod will stop workers mid-lease. Raise it on the endpoint to at least {request.MaxLeaseSeconds + 300}s."));
        }
        if (workersMax > 0 && workersMax < maxWorkers)
        {
            findings.Add(Finding("warning", $"Max Workers is {maxWorkers}, but the endpoint allows only {workersMax}. Scaling will stop at {workersMax}."));
        }
        if (image.Length > 0 && !image.Contains("swarmui-worker-runpod", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding("warning", $"The endpoint runs '{image}', not the Hartsy RunPod worker (kalebbroo/swarmui-worker-runpod). Other images will not answer lease requests."));
        }
        if (image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) || (image.Length > 0 && !image.Contains(':')))
        {
            findings.Add(Finding("warning", $"The endpoint's image '{image}' is not pinned to a version. Pin a release tag so workers do not change underneath you."));
        }
        return findings;
    }

    /// <summary>Builds one validation finding.</summary>
    static JObject Finding(string level, string message)
    {
        return new JObject { ["level"] = level, ["message"] = message };
    }

    // ── RunPod API ────────────────────────────────────────────────────────────

    /// <summary>Submits an async job and returns its ID.</summary>
    async Task<string> SubmitJobAsync(JObject input, CancellationToken cancel)
    {
        JObject payload = new() { ["input"] = input };
        using HttpResponseMessage response = await HttpRetry.SendAsync(Http, () => Request(HttpMethod.Post, $"https://api.runpod.ai/v2/{endpointId}/run", payload), Tag, cancel);
        JObject result = await ReadJsonAsync(response, "/run", cancel);
        return result["id"]?.ToString() ?? throw new SwarmReadableErrorException($"RunPod did not return a job ID for the lease request.");
    }

    /// <summary>GETs a RunPod API URL as JSON, with readable errors.</summary>
    async Task<JObject> GetJsonAsync(string url, CancellationToken cancel)
    {
        using HttpResponseMessage response = await HttpRetry.SendAsync(Http, () => Request(HttpMethod.Get, url, null), Tag, cancel);
        return await ReadJsonAsync(response, new Uri(url).AbsolutePath, cancel);
    }

    /// <summary>Cancels a job. Best-effort: never throws. Returns true only if RunPod accepted the cancel.</summary>
    public async Task<bool> CancelJobAsync(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return false;
        }
        try
        {
            using HttpResponseMessage response = await HttpRetry.SendAsync(Http, () => Request(HttpMethod.Post, $"https://api.runpod.ai/v2/{endpointId}/cancel/{jobId}", null), Tag, CancellationToken.None);
            Logs.Debug($"{Tag} Cancel of job {jobId} answered {(int)response.StatusCode}.");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logs.Verbose($"{Tag} Cancel failed for {jobId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Builds an authenticated request.</summary>
    HttpRequestMessage Request(HttpMethod method, string url, JObject body)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>Reads a JSON response, turning RunPod's error statuses into readable messages.</summary>
    async Task<JObject> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken cancel)
    {
        string text = await response.Content.ReadAsStringAsync(cancel);
        int status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RunPodApiException(status, "RunPod rejected the API key (401/403). Check your key in User Settings, API Keys.");
        }
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new RunPodApiException(status, $"RunPod endpoint '{endpointId}' (or the job asked about) was not found. Check the Endpoint ID setting.");
        }
        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new RunPodApiException(status, "RunPod is rate limiting this account. Wait a minute and try again.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new RunPodApiException(status, $"RunPod API {what} failed ({status}): {text[..Math.Min(text.Length, 300)]}");
        }
        try
        {
            return JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new SwarmReadableErrorException($"RunPod API {what} returned something that is not JSON.");
        }
    }
}
