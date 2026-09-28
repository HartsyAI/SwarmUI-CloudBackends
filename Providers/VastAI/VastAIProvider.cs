using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>A failed Vast.ai request, with the HTTP status so callers can tell a definitive answer from a transient one.</summary>
public class VastApiException(int status, string message) : SwarmReadableErrorException(message)
{
    /// <summary>HTTP status Vast.ai (or the worker's PyWorker) answered with.</summary>
    public readonly int Status = status;

    /// <summary>True for answers that mean the session is gone for good: invalid grant, or no such session.</summary>
    public bool EndsSession => Status is 400 or 401 or 403 or 404 or 410;
}

/// <summary>
/// <see cref="ICloudProvider"/> for Vast.ai Serverless endpoints running the Hartsy Vast worker image
/// (github.com/HartsyAI/Vast-Worker-SwarmUI).
///
/// A lease is a native Vast <b>session</b>, created the way Vast's own client SDK does it
/// (vastai/serverless/client/client.py <c>start_endpoint_session</c>): <c>/route/</c> for a signed grant, then
/// <c>/session/create</c> on the worker. The session counts as that worker's load and each worker takes one,
/// so further sessions route to or recruit other workers. The worker's <c>/lease</c> route then returns its
/// HTTPS gateway address and a per-lease token.
///
/// Vast adds a full lifetime to a session's expiry on every request made with it, so renewal is conditional:
/// only when the remaining time falls below half a lifetime. When the client stops renewing, the session
/// expires, the worker revokes the token, and Vast scales the worker down.
/// </summary>
public class VastAIProvider(string apiKey, string endpointName) : ICloudProvider
{
    /// <summary>Client for Vast's control plane (system trust store).</summary>
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Vast's serverless routing base URL.</summary>
    const string RouteBase = "https://run.vast.ai";

    /// <summary>Vast's console API base URL.</summary>
    const string ConsoleBase = "https://console.vast.ai";

    /// <summary>Load one session represents. Matches the worker's calibration (lease_controller.SESSION_COST).</summary>
    public const int SessionCost = 100;

    /// <summary>The lease protocol this extension speaks.</summary>
    public const int RequiredProtocol = 2;

    /// <summary>
    /// Shortest session lifetime used, in seconds. Renewals happen at most every 15s and only once half a lifetime
    /// (+15s) remains, so a shorter lifetime could lapse between renewals and cut off a running generation.
    /// </summary>
    public const int MinSessionLifetime = 60;

    /// <summary>Log prefix.</summary>
    const string Tag = "[VastAI]";

    /// <summary>Per-endpoint key used for /route/, looked up once by endpoint name.</summary>
    string EndpointApiKey;

    /// <summary>TLS relays per lease, disposed when the lease is released.</summary>
    readonly ConcurrentDictionary<string, VastTlsRelay> Relays = new();

    /// <inheritdoc/>
    public string ProviderName => "Vast.ai";

    /// <inheritdoc/>
    public string ApiKeyType => "vastai_api";

    // ── Leases ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<CloudWorkerInfo> AcquireWorkerAsync(LeaseRequest request, CancellationToken cancel)
    {
        await ResolveEndpointKeyAsync(cancel);
        DateTime deadline = DateTime.UtcNow.AddSeconds(request.StartupTimeoutSec);
        int pollMs = Math.Clamp(request.PollIntervalMs, 1000, 15000);
        // request_idx holds our place in the engine's queue; dropping it on a retry sends us to the back.
        int requestIdx = 0;
        string lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            // Before a session exists nothing is held for us, so withdrawing is just stopping.
            cancel.ThrowIfCancellationRequested();
            JObject grant = await RouteAsync(requestIdx, cancel);
            requestIdx = grant["request_idx"]?.Value<int>() ?? requestIdx;
            string workerUrl = grant["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(workerUrl))
            {
                string status = grant["status"]?.ToString() ?? "waiting";
                if (status != lastStatus)
                {
                    Logs.Info($"{Tag} Waiting for a worker on endpoint '{endpointName}' (engine status: {status})...");
                    lastStatus = status;
                }
                await Task.Delay(pollMs, cancel);
                continue;
            }
            int lifetime = Math.Max(request.IdleSeconds, MinSessionLifetime);
            JObject session = await CreateSessionAsync(workerUrl, grant, lifetime);
            if (session is null)
            {
                // That worker already holds a session: ask the engine again, which routes elsewhere or recruits.
                requestIdx = 0;
                await Task.Delay(pollMs, cancel);
                continue;
            }
            CloudWorkerInfo worker = new()
            {
                LeaseId = session["session_id"].ToString(),
                SessionAuth = grant,
                LeaseLifetime = lifetime
            };
            try
            {
                JObject lease = await LeaseCallAsync(workerUrl, worker, Program.GlobalProgramCancel);
                ApplyLease(worker, lease);
                await RefreshExpirationAsync(worker, CancellationToken.None);
                Logs.Info($"{Tag} Session {worker.LeaseId} holds worker {worker.WorkerId}.");
                return worker;
            }
            catch (Exception)
            {
                await ReleaseLeaseAsync(worker);
                throw;
            }
        }
        throw new SwarmReadableErrorException($"No Vast.ai worker became available for endpoint '{endpointName}' within {request.StartupTimeoutSec}s (last engine status: {lastStatus ?? "unknown"}). Check the endpoint's max workers and that its workergroup can find matching offers.");
    }

    /// <summary>Opens a session on the routed worker. Returns null if that worker is already full.</summary>
    async Task<JObject> CreateSessionAsync(string workerUrl, JObject grant, int lifetime)
    {
        JObject envelope = new()
        {
            ["auth_data"] = grant,
            ["session_id"] = null,
            ["payload"] = new JObject
            {
                ["lifetime"] = lifetime,
                ["on_close_route"] = "/lease/end",
                ["on_close_payload"] = new JObject()
            }
        };
        using HttpResponseMessage response = await VastTls.Http.PostAsync($"{workerUrl.TrimEnd('/')}/session/create", Json(envelope), Program.GlobalProgramCancel);
        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            Logs.Debug($"{Tag} Worker at {workerUrl} already holds a session; re-routing.");
            return null;
        }
        JObject body = await ReadJsonAsync(response, "session/create");
        if (body["session_id"] is null)
        {
            throw new SwarmReadableErrorException($"Vast.ai worker did not open a session: {body}");
        }
        return body;
    }

    /// <summary>Calls the worker's /lease route through the session (which also extends the session by one lifetime).</summary>
    async Task<JObject> LeaseCallAsync(string workerUrl, CloudWorkerInfo worker, CancellationToken cancel)
    {
        JObject envelope = new()
        {
            ["auth_data"] = worker.SessionAuth,
            ["session_id"] = worker.LeaseId,
            ["payload"] = new JObject { ["session_id"] = worker.LeaseId }
        };
        using HttpResponseMessage response = await VastTls.Http.PostAsync($"{workerUrl.TrimEnd('/')}/lease", Json(envelope), cancel);
        return await ReadJsonAsync(response, "lease");
    }

    /// <summary>Validates a /lease answer and copies it onto the worker info.</summary>
    internal static void ApplyLease(CloudWorkerInfo worker, JObject lease)
    {
        if (lease["success"]?.Value<bool>() is not true)
        {
            throw new SwarmReadableErrorException($"Vast.ai worker refused the lease: {lease["error"] ?? lease}");
        }
        int protocol = lease["protocol"]?.Value<int>() ?? 0;
        string publicUrl = lease["public_url"]?.ToString();
        string token = lease["token"]?.ToString();
        if (protocol < RequiredProtocol || string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(token))
        {
            throw new SwarmReadableErrorException("The Vast.ai worker image is too old for this version of Cloud Backends. Use an image built from kalebbroo/swarmui-worker-vast.");
        }
        worker.PublicUrl = publicUrl.TrimEnd('/');
        worker.Token = token;
        worker.WorkerId = lease["worker_id"]?.ToString();
        worker.Protocol = protocol;
    }

    /// <summary>Reads the session's current expiry from the worker without extending it.</summary>
    async Task<bool> RefreshExpirationAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        JObject body = new() { ["session_id"] = worker.LeaseId, ["session_auth"] = worker.SessionAuth };
        using HttpResponseMessage response = await VastTls.Http.PostAsync($"{SessionUrl(worker)}/session/get", Json(body), cancel);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone)
        {
            return false;
        }
        JObject data = await ReadJsonAsync(response, "session/get");
        worker.LeaseExpiration = data["expiration"]?.Value<double>() ?? worker.LeaseExpiration;
        worker.LeaseLifetime = data["lifetime"]?.Value<double>() ?? worker.LeaseLifetime;
        return true;
    }

    /// <summary>The PyWorker's own base URL, from the session's grant.</summary>
    static string SessionUrl(CloudWorkerInfo worker)
    {
        return (worker.SessionAuth?["url"]?.ToString() ?? throw new SwarmReadableErrorException("Vast.ai session has no worker URL.")).TrimEnd('/');
    }

    /// <inheritdoc/>
    public async Task<bool> IsLeaseActiveAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        try
        {
            bool active = await RefreshExpirationAsync(worker, cancel);
            worker.FailedLeaseChecks = 0;
            return active;
        }
        catch (VastApiException ex) when (ex.EndsSession)
        {
            // A definite answer that the session or its grant is gone: end the lease now, so its place is freed.
            Logs.Debug($"{Tag} Session {worker.LeaseId} is no longer valid ({ex.Status}); treating the lease as ended.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or VastApiException)
        {
            // The check talks to the worker itself, so a network blip and a dead worker look alike. One failure keeps
            // the lease (ending it would interrupt a healthy worker's generation and lease a replacement); only a run of
            // them ends it.
            worker.FailedLeaseChecks++;
            bool gone = IsUnreachableForGood(worker.FailedLeaseChecks);
            Logs.Debug($"{Tag} Could not reach the worker for session {worker.LeaseId} ({ex.Message}); {(gone ? "treating the lease as ended" : $"keeping it (failure {worker.FailedLeaseChecks} of {UnreachableChecksBeforeEnd})")}.");
            return !gone;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Each renewal adds a whole lifetime, so it only happens once less than half a lifetime remains.
    /// That keeps an in-use worker alive while bounding the paid tail after the last use to 1.5 lifetimes.</remarks>
    public async Task RenewLeaseAsync(CloudWorkerInfo worker, CancellationToken cancel)
    {
        if (!NeedsRenewal(worker.LeaseExpiration, worker.LeaseLifetime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0))
        {
            return;
        }
        // One renewal per worker at a time, re-checked inside: every /lease call adds a whole lifetime, so two
        // concurrent callers that both saw the old expiry would stack paid lifetimes onto an idle worker.
        await worker.RenewLock.WaitAsync(cancel);
        try
        {
            if (!NeedsRenewal(worker.LeaseExpiration, worker.LeaseLifetime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0))
            {
                return;
            }
            await LeaseCallAsync(SessionUrl(worker), worker, cancel);
            await RefreshExpirationAsync(worker, cancel);
            Logs.Debug($"{Tag} Renewed session {worker.LeaseId}.");
        }
        finally
        {
            worker.RenewLock.Release();
        }
    }

    /// <summary>Lease checks in a row that must fail to reach a worker before its lease is treated as ended (about a minute, at one check every 15s).</summary>
    public const int UnreachableChecksBeforeEnd = 4;

    /// <summary>True once the worker has been unreachable for <see cref="UnreachableChecksBeforeEnd"/> checks in a row.</summary>
    internal static bool IsUnreachableForGood(int failedChecks)
    {
        return failedChecks >= UnreachableChecksBeforeEnd;
    }

    /// <summary>
    /// True once less than half a lifetime (plus a 15s margin for clock skew and the renewal's own round trip) is
    /// left. Renewing any earlier would stack whole lifetimes onto the expiry and keep a worker billing after use.
    /// </summary>
    internal static bool NeedsRenewal(double expiration, double lifetime, double now)
    {
        return expiration - now <= lifetime / 2 + 15;
    }

    /// <inheritdoc/>
    public async Task ReleaseLeaseAsync(CloudWorkerInfo worker)
    {
        if (Relays.TryRemove(worker.LeaseId ?? "", out VastTlsRelay relay))
        {
            await relay.DisposeAsync();
        }
        if (worker.LeaseId is null || worker.SessionAuth is null)
        {
            return;
        }
        try
        {
            JObject body = new() { ["session_id"] = worker.LeaseId, ["session_auth"] = worker.SessionAuth };
            using HttpResponseMessage response = await VastTls.Http.PostAsync($"{SessionUrl(worker)}/session/end", Json(body), CancellationToken.None);
            Logs.Debug($"{Tag} Ended session {worker.LeaseId} ({(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            // The session still expires on its own within one lifetime.
            Logs.Verbose($"{Tag} Ending session {worker.LeaseId} failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public Task<string> GetConnectUrlAsync(CloudWorkerInfo worker)
    {
        VastTlsRelay relay = Relays.GetOrAdd(worker.LeaseId, _ => new VastTlsRelay(worker.PublicUrl));
        return Task.FromResult(relay.LocalUrl);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (VastTlsRelay relay in Relays.Values)
        {
            _ = relay.DisposeAsync();
        }
        Relays.Clear();
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            throw new SwarmReadableErrorException("No Vast.ai endpoint name is set. Set 'EndpointId' in the backend settings to your serverless endpoint's name.");
        }
        await ConsoleApiAsync(HttpMethod.Get, "/api/v0/users/current/", cancel);
        await ResolveEndpointKeyAsync(cancel);
    }

    /// <inheritdoc/>
    public async Task<JArray> CheckEndpointAsync(LeaseRequest request, int maxWorkers, CancellationToken cancel)
    {
        JArray findings = [];
        JObject endpoint = await FindEndpointAsync(cancel);
        double minLoad = endpoint["min_load"]?.Value<double>() ?? 0;
        int coldWorkers = endpoint["cold_workers"]?.Value<int>() ?? 0;
        int endpointMax = endpoint["max_workers"]?.Value<int>() ?? 0;
        double inactivity = endpoint["inactivity_timeout"]?.Value<double>() ?? 0;
        if (minLoad > 0)
        {
            findings.Add(Finding("error", $"The endpoint's Min Load is {minLoad}, which keeps a worker running at all times. Set it to 0 so the endpoint can scale to zero."));
        }
        if (inactivity <= 0)
        {
            findings.Add(Finding("warning", "The endpoint has no Inactivity Timeout, so it never scales all the way to zero. Set one (e.g. 300 seconds)."));
        }
        if (coldWorkers > 0)
        {
            findings.Add(Finding("warning", $"The endpoint keeps {coldWorkers} stopped (cold) worker(s), which bill for storage while idle. Set Min Workers to 0 unless you want faster cold starts."));
        }
        if (endpointMax > 0 && endpointMax < maxWorkers)
        {
            findings.Add(Finding("warning", $"Max Workers is {maxWorkers}, but the endpoint allows only {endpointMax}. Scaling will stop at {endpointMax}."));
        }
        return findings;
    }

    /// <summary>Builds one validation finding.</summary>
    static JObject Finding(string level, string message)
    {
        return new JObject { ["level"] = level, ["message"] = message };
    }

    // ── Vast.ai routing and console API ───────────────────────────────────────

    /// <summary>Asks the serverless engine for a worker. Returns the signed grant (empty while none is ready).</summary>
    async Task<JObject> RouteAsync(int requestIdx, CancellationToken cancel)
    {
        string key = EndpointApiKey ?? apiKey;
        JObject payload = new()
        {
            ["endpoint"] = endpointName,
            ["cost"] = SessionCost,
            ["api_key"] = key,
            ["request_idx"] = requestIdx,
            // Matches the official client (vastai/serverless/client/endpoint.py Endpoint._route).
            ["replay_timeout"] = 60.0
        };
        using HttpResponseMessage response = await HttpRetry.SendAsync(Http, () =>
        {
            // The official client also sends the key as a query parameter (client/connection.py _make_request).
            HttpRequestMessage request = new(HttpMethod.Post, $"{RouteBase}/route/?api_key={Uri.EscapeDataString(key)}") { Content = Json(payload) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }, Tag, cancel);
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            return [];
        }
        return await ReadJsonAsync(response, "route");
    }

    /// <summary>Finds this backend's endpoint in the account's endpoint list.</summary>
    async Task<JObject> FindEndpointAsync(CancellationToken cancel)
    {
        JToken result = await ConsoleApiAsync(HttpMethod.Get, "/api/v0/endptjobs/", cancel);
        JArray endpoints = result?["results"] as JArray ?? result as JArray ?? [];
        List<string> names = [];
        foreach (JObject endpoint in endpoints.OfType<JObject>())
        {
            string name = endpoint["endpoint_name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
            if (string.Equals(name, endpointName, StringComparison.OrdinalIgnoreCase))
            {
                return endpoint;
            }
        }
        throw new SwarmReadableErrorException($"Vast.ai has no serverless endpoint named '{endpointName}' on this account. Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.");
    }

    /// <summary>Looks up (once) the per-endpoint key that /route/ expects.</summary>
    async Task ResolveEndpointKeyAsync(CancellationToken cancel)
    {
        if (EndpointApiKey is not null)
        {
            return;
        }
        JObject endpoint = await FindEndpointAsync(cancel);
        EndpointApiKey = endpoint["api_key"]?.ToString() ?? apiKey;
    }

    /// <summary>Calls Vast's console REST API with the account key.</summary>
    async Task<JToken> ConsoleApiAsync(HttpMethod method, string path, CancellationToken cancel)
    {
        using HttpResponseMessage response = await HttpRetry.SendAsync(Http, () =>
        {
            HttpRequestMessage request = new(method, $"{ConsoleBase}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return request;
        }, Tag, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SwarmReadableErrorException("Vast.ai rejected the API key (401/403). Check your key in User Settings, API Keys.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"Vast.ai API {path} failed ({(int)response.StatusCode}): {text[..Math.Min(text.Length, 300)]}");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>JSON request content.</summary>
    static StringContent Json(JObject body)
    {
        return new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
    }

    /// <summary>Reads a JSON object response with a readable error for failures.</summary>
    static async Task<JObject> ReadJsonAsync(HttpResponseMessage response, string what)
    {
        string text = await response.Content.ReadAsStringAsync();
        int status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new VastApiException(status, $"Vast.ai refused the {what} request (401). The session or routing grant is no longer valid.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new VastApiException(status, $"Vast.ai {what} failed ({status}): {text[..Math.Min(text.Length, 300)]}");
        }
        try
        {
            return JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new SwarmReadableErrorException($"Vast.ai {what} returned something that is not JSON. Check that the endpoint runs the Hartsy Vast worker image.");
        }
    }
}
