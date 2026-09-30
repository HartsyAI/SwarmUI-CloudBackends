using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>A cloud provider API failure, carrying the HTTP status and the provider's error detail.</summary>
public class CloudApiException(string providerName, int status, string detail) : SwarmReadableErrorException($"{providerName} API error {status}: {detail}")
{
    /// <summary>HTTP status code the provider answered with.</summary>
    public int Status = status;

    /// <summary>Provider-reported error detail (RFC 9457 'detail' where available, else the raw body).</summary>
    public string Detail = detail;
}

/// <summary>
/// Shared JSON-over-HTTP plumbing for cloud provider REST APIs (bearer auth, readable error
/// translation), plus the helpers every instance-renting provider needs: env parsing, the
/// wait-for-SwarmUI readiness gate, and a short-TTL status cache.
/// </summary>
public class CloudApiClient(string providerName, string apiBase, string apiKey, Func<int, string, Exception> errorMapper = null)
{
    /// <summary>Shared HTTP client for provider REST calls.</summary>
    public static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Human-readable provider name used in logs and error messages.</summary>
    public string ProviderName => providerName;

    /// <summary>
    /// Calls the provider's REST API, translating failures into readable errors. A non-JSON success
    /// body yields null. With <paramref name="allowNotFound"/>, a 404 yields null instead of throwing.
    /// The provider's <c>errorMapper</c> may claim specific (status, detail) pairs; anything unmapped
    /// becomes a <see cref="CloudApiException"/>.
    /// </summary>
    public async Task<JToken> ApiAsync(HttpMethod method, string path, JObject body = null, CancellationToken cancel = default, bool allowNotFound = false)
    {
        using HttpRequestMessage request = new(method, $"{apiBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        }
        Logs.Debug($"[{providerName}] {method} {path}");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
        {
            return null;
        }
        if (response.IsSuccessStatusCode)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return JToken.Parse(text);
            }
            catch (Exception)
            {
                return null;
            }
        }
        int status = (int)response.StatusCode;
        // RFC 9457 problem responses carry the useful part in 'detail'; fall back to the raw body.
        string detail = text;
        try
        {
            JObject problem = JObject.Parse(text);
            Logs.Debug($"[{providerName}] API error {status} response fields: {string.Join(", ", problem.Properties().Select(p => $"{p.Name}:{p.Value.Type}"))}");
            detail = problem["detail"]?.ToString() ?? text;
            string fields = ValidationErrors(problem);
            if (fields is not null)
            {
                detail = $"{detail.TrimEnd('.')}: {fields}";
            }
        }
        catch (Exception)
        {
            // Intentionally empty.
        }
        throw errorMapper?.Invoke(status, detail) ?? new CloudApiException(providerName, status, detail);
    }

    /// <summary>Parses newline or comma separated KEY=VALUE pairs into a JSON object.</summary>
    public static JObject ParseEnv(string raw)
    {
        JObject result = [];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }
        foreach (string line in raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        return result;
    }

    /// <summary>
    /// Waits until the SwarmUI at <paramref name="publicUrl"/> answers its API. An instance's proxy or
    /// port mapping answers as soon as the container's networking exists, well before the service inside
    /// is listening, so this is the real readiness gate. On timeout, throws a readable error ending in
    /// <paramref name="failureHint"/> (provider-specific advice on what to check).
    /// </summary>
    /// <param name="publicUrl">The instance's SwarmUI (gateway) address.</param>
    /// <param name="deadline">When to give up.</param>
    /// <param name="failureHint">Provider-specific advice appended to the timeout error.</param>
    /// <param name="cancel">Cancels the wait.</param>
    /// <param name="workerToken">The instance's gateway token, sent as a Bearer token, or null for an unsecured SwarmUI.</param>
    /// <param name="client">HTTP client to use (e.g. one that trusts Vast.ai's CA), or null for the default.</param>
    public async Task WaitForSwarmAsync(string publicUrl, DateTime deadline, string failureHint, CancellationToken cancel = default, string workerToken = null, HttpClient client = null)
    {
        Exception last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject session = await (client ?? Http).PostJson($"{publicUrl.TrimEnd('/')}/API/GetNewSession", [], workerToken is null ? null : req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken), cancel);
                if (session?["error_id"]?.ToString() == "worker_unauthorized")
                {
                    throw new SwarmReadableErrorException($"The SwarmUI at {publicUrl} refused this backend's token. If you created that instance yourself, set its SWARMUI_WORKER_TOKEN to match, or let this backend create the instance.");
                }
                if (!string.IsNullOrWhiteSpace(session?["session_id"]?.ToString()))
                {
                    return;
                }
            }
            catch (SwarmReadableErrorException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
            Logs.Verbose($"[{providerName}] Waiting for SwarmUI on {publicUrl} to answer...");
            await Task.Delay(5000, cancel);
        }
        throw new SwarmReadableErrorException($"Instance is running but SwarmUI at {publicUrl} did not answer before the startup timeout. {failureHint}{(last is null ? "" : $" Last error: {last.Message}")}");
    }

    /// <summary>
    /// The field-level reasons in a validation problem ('errors' or 'invalid-params'): "path: message" for structured entries,
    /// the text for plain ones. Rejected values are never included, and token-shaped text is masked, since they can echo secrets.
    /// </summary>
    internal static string ValidationErrors(JObject problem)
    {
        JArray list = problem["errors"] as JArray ?? problem["invalid-params"] as JArray;
        if (list is null || list.Count == 0)
        {
            return null;
        }
        IEnumerable<string> items = list.Select(item =>
        {
            if (item is not JObject e)
            {
                // Plain-text errors may quote the rejected value, so anything shaped like a token is masked.
                return System.Text.RegularExpressions.Regex.Replace(item.ToString(), "[A-Za-z0-9_-]{32,}", "***");
            }
            string path = (e["path"] ?? e["location"] ?? e["field"] ?? e["name"])?.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
            string message = (e["message"] ?? e["reason"] ?? e["msg"])?.ToString();
            return string.IsNullOrWhiteSpace(path) ? message : $"{path}: {message}";
        }).Where(m => !string.IsNullOrWhiteSpace(m)).Take(5);
        string joined = string.Join("; ", items);
        return joined.Length == 0 ? null : joined;
    }
}

/// <summary>
/// Short-TTL cache around a provider's instance-status lookup, so a UI status poll does not hammer the
/// provider's rate-limited API. Force-refresh bypasses it, e.g. right after a Start/Stop action.
/// </summary>
public class CloudStatusCache
{
    /// <summary>How long a cached status stays fresh.</summary>
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>Single-flights status fetches.</summary>
    readonly SemaphoreSlim Lock = new(1, 1);

    CloudInstanceStatus Cached;

    DateTime Expiry = DateTime.MinValue;

    /// <summary>Returns the cached status, or fetches (single-flight) when expired or forced.</summary>
    public async Task<CloudInstanceStatus> GetAsync(bool forceRefresh, Func<Task<CloudInstanceStatus>> fetch, CancellationToken cancel = default)
    {
        if (!forceRefresh && Cached is not null && DateTime.UtcNow < Expiry)
        {
            return Cached;
        }
        await Lock.WaitAsync(cancel);
        try
        {
            if (!forceRefresh && Cached is not null && DateTime.UtcNow < Expiry)
            {
                return Cached;
            }
            Cached = await fetch();
            Expiry = DateTime.UtcNow.Add(Ttl);
            return Cached;
        }
        finally
        {
            Lock.Release();
        }
    }
}
