namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Where a started cloud instance can be reached, and what to call it.</summary>
public class CloudInstanceInfo
{
    /// <summary>Publicly reachable base URL of the SwarmUI running on the instance.</summary>
    public string PublicUrl;

    /// <summary>Provider-side identifier, for example a RunPod pod ID.</summary>
    public string InstanceId;

    /// <summary>Human-readable description of the hardware, used in the backend title.</summary>
    public string Description;
}

/// <summary>
/// A cloud provider that rents a whole instance (a "pod", a VM) which stays up until stopped, as
/// opposed to a serverless worker that is woken per request.
///
/// Implementations do one job: drive the provider's API to get a SwarmUI reachable at a URL, and to
/// shut it down again. Everything after that (sessions, models, generation) is handled by SwarmUI's
/// own <c>SwarmSwarmBackend</c>, which the backend attaches to that URL.
/// </summary>
public interface ICloudInstanceProvider : IDisposable
{
    /// <summary>Human-readable provider name shown in logs and titles.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Provider-side ID of the instance this provider is bound to, or empty until one is resolved.
    /// Set as soon as an instance is created or found - even if the start later fails - so the backend
    /// can remember it for reattachment (a created instance bills whether or not the start finished).
    /// </summary>
    string ActiveInstanceId { get; }

    /// <summary>
    /// Cheap check that credentials and configuration are usable, called before anything is started.
    /// Throw <see cref="SwarmUI.Utils.SwarmReadableErrorException"/> with an actionable message on failure.
    /// </summary>
    Task ValidateAsync(CancellationToken cancel = default);

    /// <summary>
    /// Starts (or creates, or resumes) the instance and returns once its SwarmUI is reachable.
    /// May block for minutes on a cold start.
    /// </summary>
    Task<CloudInstanceInfo> StartInstanceAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default);

    /// <summary>Releases the instance, stopping or destroying it according to the provider's configuration.</summary>
    Task ReleaseInstanceAsync(CancellationToken cancel = default);

    /// <summary>
    /// Live status of the instance (state, GPU, cost/hr, uptime), or null if no instance has been
    /// resolved yet. Cached briefly unless <paramref name="forceRefresh"/> is set, so UI polls do not
    /// hammer the provider's rate-limited API.
    /// </summary>
    Task<CloudInstanceStatus> GetStatusAsync(bool forceRefresh = false, CancellationToken cancel = default);

    /// <summary>The running instance's hourly price, for the spend failsafe, or null if unknown.</summary>
    Task<double?> GetCostPerHourAsync(CancellationToken cancel = default);

    /// <summary>
    /// Binds the provider to an instance an earlier SwarmUI run left running, or was still starting, and returns its status; null when it is
    /// gone or stopped. A provider that cannot do this returns null, so its instances are never kept through a restart.
    /// </summary>
    Task<CloudInstanceStatus> AdoptInstanceAsync(string instanceId, CancellationToken cancel = default) => Task.FromResult<CloudInstanceStatus>(null);

    /// <summary>
    /// Waits for the SwarmUI on the instance the provider is already bound to and returns where it answers, like
    /// <see cref="StartInstanceAsync"/> but never creating, starting or resuming anything. Null when the provider cannot; throws if it does not answer in time.
    /// </summary>
    Task<CloudInstanceInfo> ConnectInstanceAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default) => Task.FromResult<CloudInstanceInfo>(null);
}
