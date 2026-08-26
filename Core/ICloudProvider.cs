namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Abstraction over a cloud GPU provider (RunPod Serverless, RunPod Pods, Vast.ai, …).
///
/// Implementations are **per-instance**: construct with the API key and endpoint identifier
/// baked in so callers never need to thread credentials through method parameters.
///
/// The base backend (<see cref="CloudBackendBase"/>) owns all shared SwarmUI lifecycle logic;
/// a provider only needs to know how to wake a worker, keep it alive, and tear it down.
/// </summary>
public interface ICloudProvider : IDisposable
{
    /// <summary>Human-readable provider name shown in logs and status responses.</summary>
    string ProviderName { get; }

    /// <summary>Key type string registered in SwarmUI's upstream API key store (e.g. "runpod_api").</summary>
    string ApiKeyType { get; }

    /// <summary>
    /// Wake a worker and block until SwarmUI is accessible on it.
    /// Returns a <see cref="CloudWorkerInfo"/> with the worker's public URL and session ID.
    /// </summary>
    Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default);

    /// <summary>
    /// Start or extend keepalive for an active worker.
    /// Called after wakeup and again when the keepalive timer nears expiry.
    /// The <paramref name="cancel"/> token is cancelled by <see cref="StopKeepaliveAsync"/>.
    /// </summary>
    /// <returns>True if keepalive is actually established. False means the worker may be reaped early,
    /// so the caller must not record a long keepalive expiry.</returns>
    Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default);

    /// <summary>Stop keepalive jobs or background loops. Called on backend shutdown.</summary>
    Task StopKeepaliveAsync();

    /// <summary>
    /// Cheap validation of credentials and endpoint reachability, called once at backend init.
    /// Throw <see cref="SwarmUI.Utils.SwarmReadableErrorException"/> on bad key / missing endpoint.
    /// Default: no validation.
    /// </summary>
    Task ValidateAsync(CancellationToken cancel = default) => Task.CompletedTask;
}
