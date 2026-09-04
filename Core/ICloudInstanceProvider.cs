namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Where a started cloud instance can be reached, and what to call it.</summary>
public class CloudInstanceInfo
{
    /// <summary>Publicly reachable base URL of the SwarmUI running on the instance.</summary>
    public string PublicUrl { get; set; }

    /// <summary>Provider-side identifier, for example a RunPod pod ID.</summary>
    public string InstanceId { get; set; }

    /// <summary>Human-readable description of the hardware, used in the backend title.</summary>
    public string Description { get; set; }
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

    /// <summary>
    /// Current hourly billing rate for the running instance, if the provider can report one. Used only
    /// for the optional spend-cap failsafe in <see cref="CloudInstanceBackendBase"/> - return null if
    /// unknown, not yet available, or the provider doesn't bill this way (the failsafe simply skips the
    /// spend check in that case; the runtime-based cap still works regardless).
    /// </summary>
    Task<double?> GetCostPerHourAsync(CancellationToken cancel = default);
}
