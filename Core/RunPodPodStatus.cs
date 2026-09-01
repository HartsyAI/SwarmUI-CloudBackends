using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Live status snapshot of a RunPod pod, projected from the RunPod v2 <c>GET /pods/{id}</c> response
/// (verified against https://docs.runpod.io/api-reference-v2/pods/get-a-pod).
/// </summary>
public class RunPodPodStatus
{
    public string PodId { get; set; }

    /// <summary>One of PROVISIONING, STARTING, RUNNING, EXITED, ERROR, TERMINATED.</summary>
    public string Status { get; set; }

    public string GpuId { get; set; }
    public int GpuCount { get; set; }

    /// <summary>Hourly USD rate. 0 when not running.</summary>
    public double CostPerHour { get; set; }

    /// <summary>Seconds since the pod last started. 0 until RunPod reports a live runtime.</summary>
    public int UptimeSeconds { get; set; }

    /// <summary>Actions RunPod currently permits on this pod (its own "actions" field: start/stop/restart/terminate).</summary>
    public string[] AllowedActions { get; set; } = [];

    /// <summary>Constructed proxy URL - RunPod's pod object never includes this directly.</summary>
    public string PublicUrl { get; set; }

    public static RunPodPodStatus FromPod(string podId, JObject pod, int swarmUIPort)
    {
        if (pod is null) { return null; }
        return new RunPodPodStatus
        {
            PodId = podId,
            Status = pod["status"]?.ToString() ?? "UNKNOWN",
            GpuId = pod["gpu"]?["id"]?.ToString(),
            GpuCount = pod["gpu"]?["count"]?.Value<int>() ?? 0,
            CostPerHour = pod["cost"]?.Value<double>() ?? 0,
            UptimeSeconds = pod["runtime"]?["uptime"]?.Value<int>() ?? 0,
            AllowedActions = [.. (pod["actions"] as JArray ?? []).Select(a => a.ToString())],
            PublicUrl = $"https://{podId}-{swarmUIPort}.proxy.runpod.net"
        };
    }
}
