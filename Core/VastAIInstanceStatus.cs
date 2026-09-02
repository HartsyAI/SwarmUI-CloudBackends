using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Live status snapshot of a Vast.ai instance, projected from the Vast v0 <c>GET /instances/{id}/</c>
/// response (verified against the official `vastai` Python SDK/CLI source, not just prose docs -
/// <c>vastai/data/instance.py</c>'s <c>Instance</c> dataclass and <c>vastai/api/instances.py</c>).
///
/// Unlike RunPod, Vast has no documented status enum worth hardcoding against - the CLI itself just
/// prints `actual_status` raw. So it's treated here as opaque/display-only; readiness is instead gated
/// on connectivity (public_ipaddr + the mapped port actually answering), same principle
/// RunPodPodsProvider.WaitForSwarmAsync already uses.
/// </summary>
public class VastAIInstanceStatus
{
    public string InstanceId { get; set; }

    /// <summary>Opaque status string as Vast reports it (e.g. "running", "exited", "loading") - display only.</summary>
    public string Status { get; set; }

    public string GpuName { get; set; }
    public int GpuCount { get; set; }

    /// <summary>Hourly USD rate ("dph_total" - dollars per hour, total). 0 when not running.</summary>
    public double CostPerHour { get; set; }

    /// <summary>Vast reports this directly in minutes (unlike RunPod's seconds).</summary>
    public double UptimeMinutes { get; set; }

    /// <summary>Shared host's public IP, or null until the instance has one assigned.</summary>
    public string PublicIp { get; set; }

    /// <summary>The external port mapped to our requested internal port, or null until Vast assigns one.</summary>
    public int? MappedPort { get; set; }

    /// <summary>Plain-HTTP URL built from PublicIp/MappedPort, or null until both are known. No TLS, no auth of its own.</summary>
    public string PublicUrl { get; set; }

    public static VastAIInstanceStatus FromInstance(JObject inst, int internalPort)
    {
        if (inst is null) { return null; }
        string ip = inst["public_ipaddr"]?.ToString();
        int? mappedPort = null;
        // Vast's port map is Docker-inspect shaped: ports["7801/tcp"] = [{ "HostIp": ..., "HostPort": "..." }, ...]
        if (inst["ports"] is JObject ports && ports[$"{internalPort}/tcp"] is JArray bindings && bindings.Count > 0)
        {
            string hostPort = bindings[0]?["HostPort"]?.ToString();
            if (int.TryParse(hostPort, out int parsed)) { mappedPort = parsed; }
        }
        return new VastAIInstanceStatus
        {
            InstanceId = inst["id"]?.ToString(),
            Status = inst["actual_status"]?.ToString() ?? "unknown",
            GpuName = inst["gpu_name"]?.ToString(),
            GpuCount = inst["num_gpus"]?.Value<int>() ?? 0,
            CostPerHour = inst["dph_total"]?.Value<double>() ?? 0,
            UptimeMinutes = inst["uptime_mins"]?.Value<double>() ?? 0,
            PublicIp = ip,
            MappedPort = mappedPort,
            PublicUrl = (!string.IsNullOrWhiteSpace(ip) && mappedPort is not null) ? $"http://{ip}:{mappedPort}" : null
        };
    }
}
