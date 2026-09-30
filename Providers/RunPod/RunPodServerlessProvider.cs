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
///
/// Endpoints still running the version 1 image (kalebbroo/swarmui-runpod) answer the lease with "Unknown action".
/// Those are held the way Cloud Backends 1.x held them, a wakeup job then keepalive jobs, with one worker at most.
/// </summary>
public class RunPodServerlessProvider(string apiKey, string endpointId) : ICloudProvider
{
    /// <summary>Shared client for RunPod's APIs.</summary>
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>The lease protocol this extension speaks. Workers older than this are refused.</summary>
    public const int RequiredProtocol = 2;

    /// <summary>Log prefix.</summary>
    const string Tag = "[RunPodServerless]";

    /// <summary>True once the endpoint turned out to run the version 1 worker image.</summary>
    volatile bool Version1;

    /// <summary>Guards <see cref="Version1Held"/>.</summary>
    readonly object Version1Lock = new();

    /// <summary>The one version 1 worker held (or being woken), if any.</summary>
    CloudWorkerInfo Version1Held;

    /// <summary>The wakeup in progress for <see cref="Version1Held"/>, if any; concurrent callers wait for it instead of failing.</summary>
    Task Version1Waking;

    /// <inheritdoc/>
    /// <remarks>Version 1 keepalive jobs are not tied to a worker, so that image is limited to one worker.</remarks>
    public int WorkerLimit() => Version1 ? 1 : int.MaxValue;

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
        if (Version1)
        {
            return await AcquireVersion1WorkerAsync(request, cancel);
        }
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
                if (first is not null && IsVersion1Refusal(first))
                {
                    break;
                }
                if (first is not null)
                {
                    return ToWorker(jobId, first);
                }
                if (status is "COMPLETED" or "FAILED" or "CANCELLED" or "TIMED_OUT")
                {
                    JObject ended = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/status/{jobId}", CancellationToken.None);
                    string endedError = ended["error"]?.ToString() ?? "";
                    Logs.Debug($"{Tag} Lease job {jobId} ended {status}: {(endedError.Length > 300 ? endedError[..300] : endedError)}");
                    if (IsVersion1Refusal(EndedOutput(ended)) || IsVersion1JobError(ended["error"]?.ToString()))
                    {
                        break;
                    }
                    throw DescribeEndedJob(jobId, status, ended);
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
            await CancelJobConfirmedAsync(jobId);
            throw;
        }
        // Only reached when the worker answered as the version 1 image.
        return await AcquireVersion1WorkerAsync(request, cancel);
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

    /// <summary>A finished job's output object. Aggregated streaming jobs report an array of chunks; plain jobs one object.</summary>
    internal static JObject EndedOutput(JObject job)
    {
        JToken output = job?["output"];
        return output as JObject ?? (output as JArray)?.FirstOrDefault() as JObject;
    }

    /// <summary>True for the version 1 worker's answer to an action it does not know, which lists its own actions.</summary>
    internal static bool IsVersion1Refusal(JObject output)
    {
        if (output is null || output["success"]?.Value<bool>() is not false || output["available_actions"] is not JArray actions)
        {
            return false;
        }
        string error = output["error"]?.ToString() ?? "";
        return error.StartsWith("Unknown action", StringComparison.OrdinalIgnoreCase) && actions.Any(a => a.ToString() == "wakeup") && actions.Any(a => a.ToString() == "keepalive");
    }

    /// <summary>
    /// True for a failed job's error from the version 1 worker refusing the lease. RunPod fails a job whose output has an
    /// error and may keep only that text, so the action list is gone; version 1 words it "Unknown action: lease", while
    /// version 2 says "Unknown action 'lease'".
    /// </summary>
    internal static bool IsVersion1JobError(string error)
    {
        return error is not null && error.Contains("Unknown action: lease", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a readable error for a lease job that ended before streaming a worker.</summary>
    static Exception DescribeEndedJob(string jobId, string status, JObject job)
    {
        string error = job["error"]?.ToString() ?? EndedOutput(job)?["error"]?.ToString();
        if (error is not null && error.Contains("Unknown action", StringComparison.OrdinalIgnoreCase))
        {
            return new SwarmReadableErrorException("The RunPod worker image is too old for this version of Cloud Backends. Point the endpoint at kalebbroo/swarmui-worker-runpod 2.0.0 or later.");
        }
        return new SwarmReadableErrorException($"RunPod lease job {jobId} ended ({status}) before a worker was ready{(error is null ? "." : $": {error}")}");
    }

    // ── Version 1 workers ─────────────────────────────────────────────────────

    /// <summary>
    /// Holds a version 1 worker the way Cloud Backends 1.x did: a wakeup job returns the worker's address once its
    /// SwarmUI is up, then keepalive jobs keep it running. That image has no gateway, so there is no token.
    /// </summary>
    async Task<CloudWorkerInfo> AcquireVersion1WorkerAsync(LeaseRequest request, CancellationToken cancel)
    {
        if (!Version1)
        {
            Version1 = true;
            Logs.Warning($"{Tag} Endpoint {endpointId} runs the version 1 worker image. Using it in single-worker compatibility mode; point the endpoint at kalebbroo/swarmui-worker-runpod to scale out and get per-lease access tokens.");
        }
        CloudWorkerInfo worker = new() { Protocol = 1, KeepaliveSeconds = Math.Max(60, request.IdleSeconds) };
        TaskCompletionSource waking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task otherWakeup = null;
        lock (Version1Lock)
        {
            if (Version1Waking is not null && !Version1Waking.IsCompleted)
            {
                otherWakeup = Version1Waking;
            }
            // A held worker whose keepalives have all run out no longer runs, even if its release could not be confirmed.
            else if (Version1Held is not null && DateTime.UtcNow < Version1Held.KeepaliveExpiry)
            {
                throw new WorkerLimitReachedException("This RunPod endpoint runs the version 1 worker image, which supports one worker at a time, and that worker is already in use.");
            }
            else
            {
                Version1Held = worker;
                Version1Waking = waking.Task;
            }
        }
        if (otherWakeup is not null)
        {
            // Several requests arrived before the endpoint was known to be version 1: one worker is being woken for all of them.
            await otherWakeup;
            throw new WorkerLimitReachedException("This RunPod endpoint runs the version 1 worker image, which supports one worker at a time; the request waits for the worker being woken.");
        }
        try
        {
            worker.LeaseId = await SubmitJobAsync(new JObject { ["action"] = "wakeup" }, CancellationToken.None);
            Logs.Info($"{Tag} Wakeup job {worker.LeaseId} submitted for endpoint {endpointId} (version 1 worker).");
            JObject output = await WaitForWakeupAsync(worker.LeaseId, request, cancel);
            if (output["error_id"]?.ToString() == "unknown_action")
            {
                // The endpoint has since moved to the version 2 image: go back to leases.
                Version1 = false;
                throw new SwarmReadableErrorException("The RunPod endpoint's worker image changed to version 2 while in use. Generate again to lease a worker from it.");
            }
            if (output["success"]?.Value<bool>() is not true)
            {
                throw new SwarmReadableErrorException($"The RunPod worker's wakeup failed: {output["error"] ?? output}");
            }
            string publicUrl = output["public_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(publicUrl))
            {
                throw new SwarmReadableErrorException($"The RunPod worker's wakeup did not return its address. Output: {output}");
            }
            worker.PublicUrl = publicUrl.TrimEnd('/');
            worker.WorkerId = output["worker_id"]?.ToString();
            worker.SessionId = output["session_id"]?.ToString();
            // A finished wakeup leaves the worker without a job, and RunPod stops idle workers within seconds.
            // Not the acquisition's token: a worker assigned by now is kept, so it must stay alive.
            bool alive = await SubmitKeepaliveAsync(worker, CancellationToken.None);
            // Without a keepalive, only trust the worker briefly, as 1.x did.
            worker.KeepaliveExpiry = DateTime.UtcNow.AddSeconds(alive ? worker.KeepaliveSeconds : 60);
            Logs.Info($"{Tag} Version 1 worker {worker.WorkerId} ready at {worker.PublicUrl}.");
            return worker;
        }
        catch (Exception)
        {
            await CancelJobAsync(worker.LeaseId);
            await ReleaseVersion1Async(worker);
            throw;
        }
        finally
        {
            waking.TrySetResult();
        }
    }

    /// <summary>Waits for a wakeup job to finish, withdrawing it if cancelled while still queued.</summary>
    async Task<JObject> WaitForWakeupAsync(string jobId, LeaseRequest request, CancellationToken cancel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(request.StartupTimeoutSec);
        int pollMs = Math.Clamp(request.PollIntervalMs, 1000, 5000);
        using CancellationTokenSource waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, Program.GlobalProgramCancel);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SwarmReadableErrorException($"No RunPod worker answered the wakeup within {request.StartupTimeoutSec}s. Check the endpoint's max workers, GPU availability, and worker logs.");
            }
            JObject job;
            try
            {
                // Cancellable until a withdraw is requested; after that, polls finish so the status can decide withdraw or keep.
                job = await GetJsonAsync($"https://api.runpod.ai/v2/{endpointId}/status/{jobId}", cancel.IsCancellationRequested ? CancellationToken.None : cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && !Program.GlobalProgramCancel.IsCancellationRequested)
            {
                continue;
            }
            string status = job["status"]?.ToString() ?? "UNKNOWN";
            if (status == "COMPLETED")
            {
                return EndedOutput(job) ?? throw new SwarmReadableErrorException($"RunPod wakeup job {jobId} completed without output.");
            }
            if (status is "FAILED" or "CANCELLED" or "TIMED_OUT")
            {
                throw DescribeEndedJob(jobId, status, job);
            }
            if (cancel.IsCancellationRequested && status == "IN_QUEUE" && await CancelJobAsync(jobId))
            {
                // Nobody needs the worker and none has been assigned; once one is, it is kept, as for leases.
                cancel.ThrowIfCancellationRequested();
            }
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

    /// <summary>Submits one keepalive job for a version 1 worker. Returns false, logging why, if it could not.</summary>
    async Task<bool> SubmitKeepaliveAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        try
        {
            string jobId = await SubmitJobAsync(new JObject { ["action"] = "keepalive", ["duration"] = worker.KeepaliveSeconds, ["interval"] = 30 }, cancel);
            worker.KeepaliveJobs[jobId] = 0;
            Logs.Debug($"{Tag} Keepalive job {jobId} submitted for worker {worker.WorkerId} ({worker.KeepaliveSeconds}s).");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"{Tag} Could not submit a keepalive job for worker {worker.WorkerId}; it may stop early: {ex.Message}");
            return false;
        }
    }

    /// <summary>True once a version 1 worker's queued keepalives are less than half a window from running out.</summary>
    internal static bool KeepaliveDue(DateTime expiry, int keepaliveSeconds, DateTime now)
    {
        return (expiry - now).TotalSeconds < keepaliveSeconds / 2.0;
    }

    /// <summary>A version 1 worker's expiry after one more keepalive. Queued keepalives run one after another, so each adds a full window.</summary>
    internal static DateTime ExtendKeepalive(DateTime expiry, int keepaliveSeconds, DateTime now)
    {
        return (expiry > now ? expiry : now).AddSeconds(keepaliveSeconds);
    }

    /// <summary>Cancels a version 1 worker's keepalives, which stops it, and frees its one-worker place.</summary>
    async Task ReleaseVersion1Async(CloudWorkerInfo worker)
    {
        foreach (string jobId in worker.KeepaliveJobs.Keys.ToArray())
        {
            // A job whose cancel is not confirmed stays tracked: it may still be keeping the worker (and its bill) alive.
            if (await CancelJobConfirmedAsync(jobId))
            {
                worker.KeepaliveJobs.TryRemove(jobId, out _);
            }
        }
        lock (Version1Lock)
        {
            if (ReferenceEquals(Version1Held, worker) && worker.KeepaliveJobs.IsEmpty)
            {
                Version1Held = null;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsLeaseActiveAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        if (worker.Protocol == 1)
        {
            // As in 1.x: the worker is trusted until its queued keepalives run out.
            return DateTime.UtcNow < worker.KeepaliveExpiry;
        }
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
    /// <remarks>
    /// The worker ends its own lease when idle, so there is nothing to renew. A version 1 worker gets one more keepalive
    /// when less than half a window is left. Never by cancelling the running one: RunPod stops the worker running a cancelled job.
    /// </remarks>
    public async Task RenewLeaseAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        if (worker.Protocol != 1)
        {
            return;
        }
        await worker.RenewLock.WaitAsync(cancel);
        try
        {
            if (KeepaliveDue(worker.KeepaliveExpiry, worker.KeepaliveSeconds, DateTime.UtcNow) && await SubmitKeepaliveAsync(worker, cancel))
            {
                worker.KeepaliveExpiry = ExtendKeepalive(worker.KeepaliveExpiry, worker.KeepaliveSeconds, DateTime.UtcNow);
            }
        }
        finally
        {
            worker.RenewLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Cancelling a running RunPod job stops the worker running it, which is exactly what releasing means here.</remarks>
    public Task ReleaseLeaseAsync(CloudWorkerInfo worker)
    {
        if (worker.Protocol == 1)
        {
            return ReleaseVersion1Async(worker);
        }
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
        JObject template = endpoint["template"] as JObject ?? await GetTemplateAsync(endpoint["templateId"]?.ToString(), cancel);
        string image = template?["imageName"]?.ToString() ?? "";
        List<string> ports = TemplatePorts(template?["ports"]);
        Logs.Debug($"{Tag} Endpoint {endpointId} template: image '{image}', ports [{string.Join(", ", ports)}].");
        if (template is not null && !ports.Any(p => p.Equals($"{WorkerPort}/http", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(Finding("error", $"The endpoint does not expose port {WorkerPort} as HTTP, so RunPod's proxy cannot reach the worker's SwarmUI and workers never become usable. Edit the endpoint and add {WorkerPort} under Container configuration, Expose HTTP ports."));
        }
        bool version1Image = image.Contains("swarmui-runpod", StringComparison.OrdinalIgnoreCase);
        // The longest single job this backend runs: a lease, or on the version 1 image one keepalive.
        int longestJob = version1Image ? Math.Max(60, request.IdleSeconds) : request.MaxLeaseSeconds;
        if (executionTimeoutMs > 0 && executionTimeoutMs / 1000 <= longestJob)
        {
            string what = version1Image ? $"a keepalive job ({longestJob}s, from Idle Seconds)" : $"Max Lease Seconds ({longestJob}s)";
            findings.Add(Finding("error", $"The endpoint's execution timeout ({executionTimeoutMs / 1000}s) must be longer than {what}, or RunPod will stop workers mid-job. Raise it on the endpoint to at least {longestJob + 300}s."));
        }
        if (workersMax > 0 && workersMax < maxWorkers)
        {
            findings.Add(Finding("warning", $"Max Workers is {maxWorkers}, but the endpoint allows only {workersMax}. Scaling will stop at {workersMax}."));
        }
        if (image.Contains("hartsyinference", StringComparison.OrdinalIgnoreCase) && AllowsCudaBelow(endpoint["allowedCudaVersions"], 13.0))
        {
            findings.Add(Finding("error", "The endpoint runs the HartsyInference image but allows hosts older than CUDA 13.0. Its GPU kernels need a CUDA 13 driver, so workers on older hosts cannot start their backend. Edit the endpoint and set the minimum CUDA version to 13.0."));
        }
        if (version1Image)
        {
            findings.Add(Finding("warning", $"The endpoint runs the version 1 worker image '{image}'. It works in single-worker compatibility mode (Max Workers is held to 1, and the worker has no access token). Point the endpoint at kalebbroo/swarmui-worker-runpod to scale out and secure it."));
        }
        else if (image.Length > 0 && !image.Contains("swarmui-worker-runpod", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding("warning", $"The endpoint runs '{image}', not the Hartsy RunPod worker (kalebbroo/swarmui-worker-runpod). Other images will not answer lease requests."));
        }
        if (!version1Image && (image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) || (image.Length > 0 && !image.Contains(':'))))
        {
            findings.Add(Finding("warning", $"The endpoint's image '{image}' is not pinned to a version. Pin a release tag so workers do not change underneath you."));
        }
        return findings;
    }

    /// <summary>
    /// An endpoint's template. The endpoint API documents includeTemplate but in practice returns only templateId, so it is
    /// fetched by ID. Null if it cannot be read, which only skips the checks that need it.
    /// </summary>
    async Task<JObject> GetTemplateAsync(string templateId, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }
        try
        {
            return await GetJsonAsync($"https://rest.runpod.io/v1/templates/{templateId}?includeEndpointBoundTemplates=true", cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Debug($"{Tag} Could not read template {templateId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True if an endpoint's allowed CUDA versions (none listed means any) include one below <paramref name="minimum"/>.</summary>
    internal static bool AllowsCudaBelow(JToken allowed, double minimum)
    {
        List<double> versions = [.. TemplatePorts(allowed).Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0)];
        return versions.Count == 0 || versions.Any(v => v < minimum);
    }

    /// <summary>The worker's gateway port, which the endpoint must expose as HTTP for RunPod's proxy to reach it.</summary>
    public const int WorkerPort = 7801;

    /// <summary>A template's exposed ports, e.g. "7801/http", whether the API sends them as a list or one comma-separated string.</summary>
    internal static List<string> TemplatePorts(JToken ports)
    {
        IEnumerable<string> raw = ports is JArray list ? list.Select(p => p.ToString()) : (ports?.ToString() ?? "").Split(',');
        return [.. raw.Select(p => p.Trim()).Where(p => p.Length > 0)];
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

    /// <summary>
    /// Cancels a job, retrying a few times until RunPod accepts. Returns false if it never did: the job may then still
    /// hold (or later get) a worker, which a lease ends itself after its startup grace and a keepalive after its window.
    /// </summary>
    async Task<bool> CancelJobConfirmedAsync(string jobId)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                // Shutting down: one attempt is all there is time for, and releasing must never throw.
                if (Program.GlobalProgramCancel.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), Program.GlobalProgramCancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            if (await CancelJobAsync(jobId))
            {
                return true;
            }
        }
        Logs.Warning($"{Tag} Could not confirm cancelling RunPod job {jobId}; if it holds a worker, that worker stops on its own when its job runs out.");
        return false;
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
