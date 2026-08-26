namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>Common worker connection info returned by all cloud providers after wakeup.</summary>
public class CloudWorkerInfo
{
    public string PublicUrl { get; set; }
    public string SessionId { get; set; }
    public string WorkerId { get; set; }
    public string Version { get; set; }
}
