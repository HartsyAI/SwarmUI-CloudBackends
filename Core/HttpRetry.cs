using System.Net;
using System.Net.Http;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Retries provider API calls the provider asked us to back off from (429) or that failed transiently
/// (502, 503, 504, connection errors). Honors <c>Retry-After</c>, otherwise backs off exponentially with jitter.
/// Everything else is returned to the caller unchanged, including 4xx errors that need a readable message.
/// </summary>
public static class HttpRetry
{
    /// <summary>Most attempts per call, including the first.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Longest single wait between attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>Shared jitter source.</summary>
    static readonly Random Jitter = new();

    /// <summary>True for statuses worth retrying.</summary>
    public static bool IsTransient(HttpStatusCode status)
    {
        return status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    }

    /// <summary>How long to wait before the given retry attempt (1-based), honoring <paramref name="retryAfter"/> when present.</summary>
    public static TimeSpan DelayFor(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is TimeSpan given && given > TimeSpan.Zero)
        {
            return given < MaxDelay ? given : MaxDelay;
        }
        double seconds = Math.Min(MaxDelay.TotalSeconds, Math.Pow(2, attempt - 1));
        double jitter;
        lock (Jitter)
        {
            jitter = Jitter.NextDouble() * 0.5;
        }
        return TimeSpan.FromSeconds(seconds * (1 + jitter));
    }

    /// <summary>Reads a Retry-After header, in either delta-seconds or HTTP-date form.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        System.Net.Http.Headers.RetryConditionHeaderValue header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (header?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// Sends a request built fresh by <paramref name="makeRequest"/> for each attempt (a request message cannot be
    /// resent), retrying transient failures. The caller owns the returned response.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> makeRequest, string logPrefix, CancellationToken cancel)
    {
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using HttpRequestMessage request = makeRequest();
                response = await client.SendAsync(request, cancel);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                TimeSpan wait = DelayFor(attempt, null);
                Logs.Debug($"{logPrefix} Request failed ({ex.Message}); retrying in {wait.TotalSeconds:0.0}s (attempt {attempt}/{MaxAttempts})");
                await Task.Delay(wait, cancel);
                continue;
            }
            if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts)
            {
                return response;
            }
            TimeSpan delay = DelayFor(attempt, RetryAfter(response));
            Logs.Info($"{logPrefix} Provider answered {(int)response.StatusCode}; backing off {delay.TotalSeconds:0.0}s (attempt {attempt}/{MaxAttempts})");
            response.Dispose();
            await Task.Delay(delay, cancel);
        }
    }
}
