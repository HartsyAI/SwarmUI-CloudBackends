namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Live status snapshot of a rented cloud instance (a RunPod pod, a Vast.ai instance), in one
/// provider-neutral shape so the WebAPI and UI handle every provider identically. Providers project
/// their own API responses into this via a static factory kept next to their JSON knowledge
/// (see RunPodPodsProvider and VastAIInstanceProvider).
/// </summary>
public class CloudInstanceStatus
{
    /// <summary>Provider-side identifier, for example a RunPod pod ID or Vast.ai contract ID.</summary>
    public string InstanceId;

    /// <summary>Provider-reported state string, display only (RunPod: RUNNING/EXITED/...; Vast: running/exited/...).</summary>
    public string Status;

    /// <summary>GPU model name or ID as the provider reports it.</summary>
    public string GpuName;

    /// <summary>Number of GPUs on the instance.</summary>
    public int GpuCount;

    /// <summary>Hourly USD rate. 0 when not running or unknown.</summary>
    public double CostPerHour;

    /// <summary>Seconds since the instance last started. Always seconds, whatever unit the provider reports in.</summary>
    public int UptimeSeconds;

    /// <summary>Actions the provider currently permits (RunPod publishes these; empty where the provider has no such list).</summary>
    public string[] AllowedActions { get; set; } = [];

    /// <summary>Base URL where the instance's SwarmUI is (or will be) reachable, or null until known.</summary>
    public string PublicUrl;
}
