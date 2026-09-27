using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>One leased cloud worker: where it is, how to authenticate to it, and how to release it.</summary>
public class CloudWorkerInfo
{
    /// <summary>Publicly reachable base URL of the worker's SwarmUI gateway.</summary>
    public string PublicUrl;

    /// <summary>Bearer token the worker's gateway accepts for this lease only. Never logged.</summary>
    public string Token;

    /// <summary>SwarmUI session ID held against the worker, for this extension's own direct API calls.</summary>
    public string SessionId;

    /// <summary>Provider-side worker identifier, for logs.</summary>
    public string WorkerId;

    /// <summary>Provider handle for the lease: a RunPod job ID or a Vast.ai session ID.</summary>
    public string LeaseId;

    /// <summary>Vast.ai only: the routing grant the session was created with, needed to renew, read and end it.</summary>
    public JObject SessionAuth;

    /// <summary>Vast.ai only: the session's lifetime in seconds, which every renewal adds to its expiry.</summary>
    public double LeaseLifetime;

    /// <summary>Vast.ai only: the session's expiry as the worker's Unix time, as last reported by the worker.</summary>
    public double LeaseExpiration;

    /// <summary>Lease protocol version the worker reported.</summary>
    public int Protocol;

    /// <summary>Lease checks in a row that could not reach the worker. Reset by any answer.</summary>
    public int FailedLeaseChecks;
}
