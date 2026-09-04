namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Common worker connection info returned by all cloud providers after wakeup.</summary>
public class CloudWorkerInfo
{
    /// <summary>Publicly reachable base URL of the worker's SwarmUI.</summary>
    public string PublicUrl { get; set; }

    /// <summary>Session ID held open against the worker's SwarmUI.</summary>
    public string SessionId { get; set; }

    /// <summary>Provider-side worker identifier, for logs.</summary>
    public string WorkerId { get; set; }

    /// <summary>Version string the worker reported, if any.</summary>
    public string Version { get; set; }
}
