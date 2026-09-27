using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>What a lease asks the provider for. Mirrors the backend's settings at the time of the request.</summary>
public class LeaseRequest
{
    /// <summary>Seconds a leased worker may sit with no generation before it is released.</summary>
    public int IdleSeconds;

    /// <summary>Seconds a new lease may wait for its first generation.</summary>
    public int StartupGraceSeconds;

    /// <summary>Longest a single lease may last, in seconds.</summary>
    public int MaxLeaseSeconds;

    /// <summary>Longest to wait for a worker to be assigned and answer, in seconds.</summary>
    public int StartupTimeoutSec;

    /// <summary>Polling interval while waiting, in milliseconds.</summary>
    public int PollIntervalMs;
}

/// <summary>
/// A serverless cloud GPU provider, as seen by <see cref="CloudBackendBase"/>. Implementations are per-instance,
/// constructed with the owner's API key and the endpoint baked in.
///
/// Every worker is held by exactly one <b>lease</b>: a provider-native handle (a RunPod job, a Vast.ai session)
/// that keeps that one worker assigned while it is in use and lets it go when it is not. Leases are what let
/// the provider's own autoscaler see real demand and add workers.
/// </summary>
public interface ICloudProvider : IDisposable
{
    /// <summary>Human-readable provider name shown in logs and status responses.</summary>
    string ProviderName { get; }

    /// <summary>Key type string registered in SwarmUI's upstream API key store (e.g. "runpod_api").</summary>
    string ApiKeyType { get; }

    /// <summary>
    /// Leases a worker and returns once its gateway address and token are known. Cancelling <paramref name="cancel"/>
    /// before a worker has been assigned withdraws the request so nothing is billed; once one has been assigned it
    /// is returned anyway, because it is already being paid for and is useful capacity.
    /// </summary>
    Task<CloudWorkerInfo> AcquireWorkerAsync(LeaseRequest request, CancellationToken cancel);

    /// <summary>True while the lease still holds its worker.</summary>
    Task<bool> IsLeaseActiveAsync(CloudWorkerInfo worker, CancellationToken cancel);

    /// <summary>
    /// Called while the worker is in use. Providers whose worker releases itself on idle (RunPod) do nothing;
    /// providers whose lease has a client-side lifetime (Vast.ai) extend it only when it is close to running out.
    /// </summary>
    Task RenewLeaseAsync(CloudWorkerInfo worker, CancellationToken cancel);

    /// <summary>Ends the lease now. Best-effort; never throws.</summary>
    Task ReleaseLeaseAsync(CloudWorkerInfo worker);

    /// <summary>Cheap validation of credentials and endpoint, called at backend init. Throws readable errors.</summary>
    Task ValidateAsync(CancellationToken cancel = default) => Task.CompletedTask;

    /// <summary>
    /// Checks the endpoint's provider-side configuration against this backend's settings and returns findings,
    /// each <c>{ "level": "error"|"warning", "message": ... }</c>. Empty means nothing to report.
    /// </summary>
    Task<JArray> CheckEndpointAsync(LeaseRequest request, int maxWorkers, CancellationToken cancel) => Task.FromResult(new JArray());

    /// <summary>
    /// The URL this extension should use to reach a leased worker's gateway. Usually <see cref="CloudWorkerInfo.PublicUrl"/>;
    /// Vast.ai returns a loopback relay that verifies Vast's own certificate authority.
    /// </summary>
    Task<string> GetConnectUrlAsync(CloudWorkerInfo worker) => Task.FromResult(worker.PublicUrl.TrimEnd('/'));
}
