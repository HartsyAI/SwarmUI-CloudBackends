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

/// <summary>JSON-over-HTTP client for a cloud provider's REST API: bearer auth, retries, and readable errors.</summary>
/// <param name="providerName">Provider name used in logs and error messages.</param>
/// <param name="apiBase">Base URL that request paths are appended to.</param>
/// <param name="apiKey">API key, sent as a Bearer token.</param>
/// <param name="errorMapper">Optionally turns a failed (status, detail) into a provider-specific exception, or null to use <see cref="CloudApiException"/>.</param>
public class CloudApiClient(string providerName, string apiBase, string apiKey, Func<int, string, Exception> errorMapper = null)
{
    /// <summary>Shared HTTP client for provider REST calls.</summary>
    public static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Calls the provider's REST API and returns the parsed JSON, or null for an empty or non-JSON success body.
    /// Rate limits are retried, and server errors too for GET and DELETE only. Failures throw a readable error with secrets masked.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path and query, appended to the API base.</param>
    /// <param name="body">JSON request body, or null for none.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <param name="allowNotFound">Return null on a 404 instead of throwing.</param>
    public async Task<JToken> ApiAsync(HttpMethod method, string path, JObject body = null, CancellationToken cancel = default, bool allowNotFound = false)
    {
        HttpRequestMessage MakeRequest()
        {
            HttpRequestMessage request = new(method, $"{apiBase}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            }
            return request;
        }
        Logs.Debug($"[{providerName}] {method} {path.Split('?')[0]}");
        bool idempotent = method == HttpMethod.Get || method == HttpMethod.Delete;
        using HttpResponseMessage response = await HttpRetry.SendAsync(Http, MakeRequest, $"[{providerName}]", cancel, idempotent);
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
        string detail = text;
        try
        {
            JObject problem = JObject.Parse(text);
            detail = problem["detail"]?.ToString() ?? text;
            string fields = ValidationErrors(problem);
            if (fields is not null)
            {
                detail = $"{detail.TrimEnd('.')}: {fields}";
            }
        }
        catch (Exception)
        {
        }
        detail = MaskSecrets(detail);
        throw errorMapper?.Invoke(status, detail) ?? new CloudApiException(providerName, status, detail);
    }

    /// <summary>Waits until the SwarmUI at <paramref name="publicUrl"/> answers its API. A provider's proxy answers before the service
    /// inside is listening, so this is the real readiness gate. Throws a readable error if the deadline passes or the token is refused.</summary>
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
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                JObject session = await (client ?? Http).PostJson($"{publicUrl.TrimEnd('/')}/API/GetNewSession", [], workerToken is null ? null : req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken), attempt.Token);
                if (session?["error_id"]?.ToString() == "worker_unauthorized")
                {
                    throw new SwarmReadableErrorException($"The SwarmUI at {publicUrl} refused this backend's token. If you created that instance yourself, set the worker token in its worker config to match, or let this backend create the instance.");
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
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                last = ex;
            }
            Logs.Verbose($"[{providerName}] Waiting for SwarmUI on {publicUrl} to answer...");
            await Task.Delay(5000, cancel);
        }
        throw new SwarmReadableErrorException($"Instance is running but SwarmUI at {publicUrl} did not answer before the startup timeout. {failureHint}{(last is null ? "" : $" Last error: {last.Message}")}");
    }

    /// <summary>Summarizes the field-level reasons in a validation problem ('errors' or 'invalid-params') as "path: message", or null if none.
    /// Rejected values are left out and token-shaped text is masked.</summary>
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
                return MaskSecrets(item.ToString());
            }
            string path = (e["path"] ?? e["location"] ?? e["field"] ?? e["name"])?.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
            string message = (e["message"] ?? e["reason"] ?? e["msg"])?.ToString();
            return string.IsNullOrWhiteSpace(path) ? message : $"{path}: {message}";
        }).Where(m => !string.IsNullOrWhiteSpace(m)).Take(5);
        string joined = string.Join("; ", items);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Masks anything shaped like a token or key (32+ URL-safe characters).</summary>
    internal static string MaskSecrets(string text)
    {
        return text is null ? null : System.Text.RegularExpressions.Regex.Replace(text, "[A-Za-z0-9_-]{32,}", "***");
    }
}

/// <summary>Short-lived cache of an instance's status, so UI polling does not hammer the provider's rate-limited API.</summary>
public class CloudStatusCache
{
    /// <summary>How long a cached status stays fresh.</summary>
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>Single-flights status fetches.</summary>
    readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>The last fetched status.</summary>
    CloudInstanceStatus Cached;

    /// <summary>When <see cref="Cached"/> goes stale.</summary>
    DateTime Expiry = DateTime.MinValue;

    /// <summary>Returns the cached status, or calls <paramref name="fetch"/> when it is stale or <paramref name="forceRefresh"/> is set.</summary>
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
