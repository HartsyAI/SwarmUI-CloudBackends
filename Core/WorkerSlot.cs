using SwarmUI.Backends;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>One leased serverless worker and the swarm child attached to it.</summary>
public class WorkerSlot
{
    /// <summary>The lease and how to reach the worker.</summary>
    public CloudWorkerInfo Worker;

    /// <summary>The provider that created the lease. Always used to renew, check and release it, even after the backend
    /// has rebuilt its own provider (for example on an API key change), since the lease belongs to this one's account.</summary>
    public ICloudProvider Provider;

    /// <summary>The address this extension (and the child) connects to: the worker's URL, or a local TLS relay to it.</summary>
    public string ConnectUrl;

    /// <summary>The attached owner-bound swarm child, which mirrors the worker's own backends.</summary>
    public BackendHandler.BackendData Child;

    /// <summary>When the lease was acquired.</summary>
    public DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>Earliest time (Environment.TickCount64) to ask the provider again whether the lease is still held.</summary>
    public long NextLeaseCheckTick;

    /// <summary>Earliest time (Environment.TickCount64) to consider renewing the lease again.</summary>
    public long NextRenewTick;

    /// <summary>Set once the slot is being removed, so no new work is handed to it.</summary>
    public volatile bool Removing;

    /// <summary>The worker's own generating backends, as mirrored by the child. Empty until the mirror arrives.</summary>
    public IEnumerable<BackendHandler.T2IBackendData> Grandchildren
        => Child?.AbstractBackend is SwarmSwarmBackend swarm ? swarm.ControlledNonrealBackends.Values : [];

    /// <summary>The mirrored backends that are up and able to generate.</summary>
    public IEnumerable<BackendHandler.T2IBackendData> RunningGrandchildren
        => Removing ? [] : Grandchildren.Where(d => d.Backend.Status == BackendStatus.RUNNING);

    /// <summary>True while any of this worker's backends is generating or has work queued.</summary>
    public bool InUse => Grandchildren.Any(d => d.CheckIsInUseAtAll);
}
