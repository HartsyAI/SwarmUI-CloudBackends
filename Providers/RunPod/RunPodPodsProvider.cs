using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using System.Net.Http;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>Options controlling how <see cref="RunPodPodsProvider"/> finds or creates its pod.</summary>
public class RunPodPodPlan
{
    /// <summary>Existing pod ID to use. If blank, the provider finds or creates one by <see cref="PodName"/>.</summary>
    public string PodId = "";

    /// <summary>Port SwarmUI listens on inside the pod, exposed as http so RunPod's proxy fronts it.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Pod ID remembered from a previous run, used as a reattach hint once verified live. A stale one never blocks a fresh create.</summary>
    public string PersistedId = "";

    /// <summary>Name given to created pods, and used to find one again so restarts do not pile up pods.</summary>
    public string PodName = "swarmui-cloudbackends";

    /// <summary>Docker image to create the pod from (ignored when a template is set).</summary>
    public string ImageName = "";

    /// <summary>GPU type to create the pod with, or empty for cheapest available.</summary>
    public string GpuTypeId = "";

    /// <summary>GPUs per created pod.</summary>
    public int GpuCount = 1;

    /// <summary>Container disk for a created pod, in GB.</summary>
    public int ContainerDiskGb = 50;

    /// <summary>Pod volume in GB when no network volume is attached.</summary>
    public int VolumeGb = 0;

    /// <summary>Where the volume is mounted inside the pod.</summary>
    public string VolumeMountPath = "/workspace";

    /// <summary>Network volume to attach, if any.</summary>
    public string NetworkVolumeId = "";

    /// <summary>RunPod cloud to create the pod in: SECURE or COMMUNITY.</summary>
    public string CloudType = "SECURE";

    /// <summary>Data center to create the pod in, or empty to let RunPod choose.</summary>
    public string DataCenterId = "";

    /// <summary>RunPod template to create the pod from, instead of an image.</summary>
    public string TemplateId = "";

    /// <summary>Gateway token for the pod's SwarmUI worker, sent in its worker config when creating it.</summary>
    public string WorkerToken = "";

    /// <summary>If true, releasing a pod this card found or created (Stop, a failsafe, or shutdown) terminates it; if false it is only stopped, so it can resume. A pod named in <see cref="PodId"/> is the user's own and is only ever stopped.</summary>
    public bool DeleteOnStop = true;
}

/// <summary><see cref="ICloudInstanceProvider"/> for RunPod on-demand GPU pods, on REST API v2 (https://api.runpod.io/v2). REST v1 retires 2026-11-15 and GraphQL in early 2027.
/// Starting resolves a pod (an explicit ID, else one found by name, else a new one), starts it if needed, waits for it to be RUNNING with live runtime,
/// then waits for SwarmUI on its proxy URL, https://{podId}-{port}.proxy.runpod.net.
/// Pods bill while running, so there is no keepalive: releasing stops or terminates the pod.</summary>
public class RunPodPodsProvider(string apiKey, RunPodPodPlan plan) : ICloudInstanceProvider
{
    /// <summary>Statuses a pod never leaves on its own.</summary>
    static readonly string[] TerminalStatuses = ["ERROR", "TERMINATED"];

    /// <summary>Statuses of a pod that does not run and will not run unless something starts it: a connect gives up on these instead of waiting.</summary>
    static readonly string[] StoppedStatuses = ["EXITED", "PAUSED", "DEAD", "ERROR", "TERMINATED"];

    /// <summary>Pod this provider is bound to. Resolved on first wake.</summary>
    public string ActiveInstanceId { get; private set; } = plan.PodId?.Trim() ?? "";

    /// <inheritdoc/>
    public string ProviderName => "RunPod GPU Pods";

    /// <summary>Shared HTTP plumbing, with RunPod's well-known failure statuses mapped to actionable messages.</summary>
    readonly CloudApiClient Api = new("RunPodPods", "https://api.runpod.io/v2", apiKey, (status, detail) => status switch
    {
        401 => new SwarmReadableErrorException("RunPod API key was rejected (401). Check your key in User Settings -> API Keys."),
        402 => new SwarmReadableErrorException("RunPod reports an insufficient account balance (402). Add credit before starting pods."),
        422 => new SwarmReadableErrorException($"RunPod rejected the request as invalid (422): {detail}. This is a bug in the request rather than a capacity problem."),
        429 => new SwarmReadableErrorException("RunPod rate limited this request (429). Wait a moment and retry."),
        _ => null
    });

    /// <summary>Short-lived cache of the pod's status.</summary>
    readonly CloudStatusCache StatusCache = new();

    /// <inheritdoc/>
    /// <remarks>Null until a pod has been resolved. Cached briefly unless <paramref name="forceRefresh"/> is set.</remarks>
    public async Task<CloudInstanceStatus> GetStatusAsync(bool forceRefresh = false, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return null;
        }
        return await StatusCache.GetAsync(forceRefresh, async () => StatusFromPod(await GetPodAsync(ActiveInstanceId, cancel)), cancel);
    }

    /// <summary>Projects RunPod's <c>GET /pods/{id}</c> response (https://docs.runpod.io/api-reference-v2/pods/get-a-pod) into the provider-neutral status.
    /// RunPod sends "gpu" and "runtime" as JSON null before provisioning, and a null JValue still throws on <c>?[...]</c>, so they are cast instead.</summary>
    CloudInstanceStatus StatusFromPod(JObject pod)
    {
        if (pod is null)
        {
            return null;
        }
        return new CloudInstanceStatus
        {
            InstanceId = ActiveInstanceId,
            Status = pod["status"]?.ToString() ?? "UNKNOWN",
            GpuName = (pod["gpu"] as JObject)?["id"]?.ToString(),
            GpuCount = (pod["gpu"] as JObject)?["count"]?.Value<int>() ?? 0,
            CostPerHour = pod["cost"]?.Value<double>() ?? 0,
            UptimeSeconds = (pod["runtime"] as JObject)?["uptime"]?.Value<int>() ?? 0,
            AllowedActions = [.. (pod["actions"] as JArray ?? []).Select(a => a.ToString())],
            PublicUrl = $"https://{ActiveInstanceId}-{plan.SwarmUIPort}.proxy.runpod.net"
        };
    }

    /// <inheritdoc/>
    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId) && string.IsNullOrWhiteSpace(plan.ImageName) && string.IsNullOrWhiteSpace(plan.TemplateId))
        {
            throw new SwarmReadableErrorException("Set 'ImageName' or 'TemplateId' so a pod can be created, or set 'PodId' to use an existing pod.");
        }
        await ApiAsync(HttpMethod.Get, "/pods", null, cancel);
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId) && await GetPodAsync(ActiveInstanceId, cancel) is null)
        {
            throw new SwarmReadableErrorException($"RunPod pod '{ActiveInstanceId}' was not found on this account. Check the PodId backend setting.");
        }
    }

    /// <inheritdoc/>
    public Task<CloudInstanceInfo> StartInstanceAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        return StartOrConnectAsync(maxWaitSeconds, pollIntervalMs, mayStart: true, cancel);
    }

    /// <inheritdoc/>
    public async Task<CloudInstanceInfo> ConnectInstanceAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        return string.IsNullOrWhiteSpace(ActiveInstanceId) ? null : await StartOrConnectAsync(maxWaitSeconds, pollIntervalMs, mayStart: false, cancel);
    }

    /// <summary>Resolves (or creates) the pod, starts it if <paramref name="mayStart"/>, then waits for its SwarmUI. Without <paramref name="mayStart"/> it only waits.
    /// A live runtime is the readiness gate for the pod itself (it is null until RUNNING), but the proxy answers 502 while the container boots, so SwarmUI is waited on too.</summary>
    async Task<CloudInstanceInfo> StartOrConnectAsync(int maxWaitSeconds, int pollIntervalMs, bool mayStart, CancellationToken cancel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string podId = await ResolvePodAsync(cancel);
        JObject pod = await GetPodAsync(podId, cancel) ?? throw new SwarmReadableErrorException($"RunPod pod '{podId}' no longer exists.");
        string status = pod["status"]?.ToString() ?? "";
        Logs.Info($"[RunPodPods] Pod '{podId}' status={status}");
        if (status.Equals("TERMINATED", StringComparison.OrdinalIgnoreCase))
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' has been terminated and cannot be resumed. Clear 'PodId' to let Cloud Backends create a new one.");
        }
        if (!mayStart && StoppedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' is {status}. Press Start to resume it, or Stop to release it.");
        }
        if (!status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            if (mayStart && PodAllows(pod, "start"))
            {
                Logs.Info($"[RunPodPods] Starting pod '{podId}'...");
                await ApiAsync(HttpMethod.Post, $"/pods/{podId}/action", new JObject { ["action"] = "start" }, cancel);
            }
            else if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwarmReadableErrorException($"RunPod pod '{podId}' is in an unrecoverable ERROR state. Terminate it and create a new one.");
            }
        }
        int clampedPollMs = Math.Clamp(pollIntervalMs, 2000, 15000);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                pod = await GetPodAsync(podId, cancel);
                status = pod?["status"]?.ToString() ?? "";
                if (TerminalStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    throw new SwarmReadableErrorException($"RunPod pod '{podId}' entered state {status} while starting. Check the pod's logs in the RunPod console.");
                }
                if (status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase)
                    && pod["runtime"] is JObject runtime && (runtime["uptime"]?.Value<int>() ?? 0) > 0)
                {
                    if (IsRunningWithoutGpu(pod))
                    {
                        throw new SwarmReadableErrorException($"RunPod pod '{podId}' started without a GPU: its machine's GPU went to someone else while it was stopped. Delete it in the RunPod console. For a pod the card created, the next Start then creates a fresh one (Delete On Stop avoids this).");
                    }
                    Logs.Info($"[RunPodPods] Pod '{podId}' is running (uptime {runtime["uptime"]}s).");
                    break;
                }
                Logs.Verbose($"[RunPodPods] Pod '{podId}' status={status}, waiting for it to run...");
            }
            catch (SwarmReadableErrorException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logs.Verbose($"[RunPodPods] Pod poll error: {ex.Message}");
            }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (DateTime.UtcNow >= deadline)
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' did not reach a running state within {maxWaitSeconds}s.");
        }
        string publicUrl = $"https://{podId}-{plan.SwarmUIPort}.proxy.runpod.net";
        await Api.WaitForSwarmAsync(publicUrl, deadline, $"Check that the pod runs the Hartsy RunPod worker image and that port {plan.SwarmUIPort} is exposed as http.", cancel, NullIfEmpty(plan.WorkerToken));
        string gpu = (pod?["gpu"] as JObject)?["id"]?.ToString();
        int gpuCount = (pod?["gpu"] as JObject)?["count"]?.Value<int>() ?? plan.GpuCount;
        Logs.Info($"[RunPodPods] SwarmUI is up on pod '{podId}' at {publicUrl}");
        return new CloudInstanceInfo
        {
            PublicUrl = publicUrl,
            InstanceId = podId,
            Description = string.IsNullOrWhiteSpace(gpu) ? "RunPod pod" : $"{gpuCount}x {gpu}"
        };
    }

    /// <summary>True if RunPod reports the pod with a GPU count of zero, as happens when a stopped pod resumes after its GPU was rented out.</summary>
    internal static bool IsRunningWithoutGpu(JObject pod)
    {
        return pod["gpu"] is JObject gpu && gpu["count"] is JValue count && count.Type == JTokenType.Integer && count.Value<int>() == 0;
    }

    /// <summary>True if the pod's published action list permits the given action.</summary>
    static bool PodAllows(JObject pod, string action)
    {
        return pod["actions"] is JArray actions && actions.Any(a => string.Equals(a.ToString(), action, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Releases the pod, stopping or terminating it per the plan.</summary>
    public async Task ReleaseInstanceAsync(CancellationToken cancel = default)
    {
        if (plan.DeleteOnStop && string.IsNullOrWhiteSpace(plan.PodId))
        {
            await TerminatePodAsync(cancel);
        }
        else
        {
            await StopPodAsync(cancel);
        }
    }

    /// <inheritdoc/>
    public async Task<double?> GetCostPerHourAsync(CancellationToken cancel = default)
    {
        CloudInstanceStatus status = await GetStatusAsync(forceRefresh: false, cancel);
        return status?.CostPerHour;
    }

    /// <inheritdoc/>
    public async Task<CloudInstanceStatus> AdoptInstanceAsync(string instanceId, CancellationToken cancel = default)
    {
        JObject pod = string.IsNullOrWhiteSpace(instanceId) ? null : await GetPodAsync(instanceId.Trim(), cancel);
        if (!IsAdoptable(pod))
        {
            return null;
        }
        ActiveInstanceId = instanceId.Trim();
        Logs.Info($"[RunPodPods] Reattached pod '{ActiveInstanceId}' ({pod["status"]}), kept from an earlier SwarmUI run.");
        return StatusFromPod(pod);
    }

    /// <summary>True for a pod that still exists, whatever its state: running, still starting (SwarmUI can exit mid-start), or stopped since (from the
    /// RunPod console, or by the pod itself). The card holds it until Stop or a failsafe releases it, so a stopped pod is still terminated.</summary>
    internal static bool IsAdoptable(JObject pod)
    {
        return pod is not null && !string.Equals(pod["status"]?.ToString(), "TERMINATED", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <summary>True if an existing pod runs the configured image (or no image is configured, or it cannot be told). A pod left from
    /// before the image was changed is not reused: it is left as it is, since removing it is the owner's call.</summary>
    bool RunsConfiguredImage(JObject pod)
    {
        string podImage = (pod["image"] ?? pod["imageName"])?.ToString();
        if (string.IsNullOrWhiteSpace(plan.ImageName) || !string.IsNullOrWhiteSpace(plan.TemplateId) || string.IsNullOrWhiteSpace(podImage)
            || string.Equals(podImage.Trim(), plan.ImageName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        Logs.Warning($"[RunPodPods] Not reusing pod '{pod["id"]}': it runs '{podImage}', not the configured '{plan.ImageName}'. It is left as it is; terminate it in the RunPod console if it is no longer needed.");
        return false;
    }

    /// <summary>Finds the pod to use: an explicit ID, else a remembered one, else a previously created pod by name, else creates one.
    /// A remembered ID is only a hint, verified before use. Existing pods are reused so every restart does not leave another billing pod behind.</summary>
    async Task<string> ResolvePodAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return ActiveInstanceId;
        }
        if (!string.IsNullOrWhiteSpace(plan.PersistedId))
        {
            JObject remembered = await GetPodAsync(plan.PersistedId, cancel);
            if (remembered is not null && !TerminalStatuses.Contains(remembered["status"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) && RunsConfiguredImage(remembered))
            {
                ActiveInstanceId = plan.PersistedId;
                Logs.Info($"[RunPodPods] Reattaching remembered pod '{ActiveInstanceId}'.");
                return ActiveInstanceId;
            }
            Logs.Debug($"[RunPodPods] Remembered pod '{plan.PersistedId}' is gone or terminal, ignoring it.");
        }
        foreach (JToken t in await ListPodsAsync(cancel))
        {
            if (t is JObject p && string.Equals(p["name"]?.ToString(), plan.PodName, StringComparison.OrdinalIgnoreCase)
                && !TerminalStatuses.Contains(p["status"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) && RunsConfiguredImage(p))
            {
                ActiveInstanceId = p["id"]?.ToString();
                Logs.Info($"[RunPodPods] Reusing existing pod '{ActiveInstanceId}' named '{plan.PodName}'.");
                return ActiveInstanceId;
            }
        }
        ActiveInstanceId = await CreatePodAsync(cancel);
        return ActiveInstanceId;
    }

    /// <summary>Creates a pod. v2 places one GPU type per call with no server-side fallback, so this orders candidates from the catalog and tries them until one is accepted.
    /// A network volume pins the data center. A 400 or 403 moves to the next candidate (it can mean no capacity or a bad request), and the last reason is reported if all fail.
    /// The request is never cancelled once sent, since a pod RunPod creates bills and losing its ID would leave it running.</summary>
    public async Task<string> CreatePodAsync(CancellationToken cancel = default)
    {
        string dataCenterId = plan.DataCenterId;
        if (!string.IsNullOrWhiteSpace(plan.NetworkVolumeId))
        {
            string volumeDc = await LookupVolumeDataCenterAsync(plan.NetworkVolumeId, cancel);
            if (!string.IsNullOrWhiteSpace(volumeDc))
            {
                if (!string.IsNullOrWhiteSpace(dataCenterId) && !dataCenterId.Equals(volumeDc, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Warning($"[RunPodPods] Overriding data center '{dataCenterId}' with '{volumeDc}', where network volume '{plan.NetworkVolumeId}' lives.");
                }
                dataCenterId = volumeDc;
            }
        }
        List<string> candidates = await GpuCandidatesAsync(dataCenterId, cancel);
        string lastDetail = null;
        foreach (string gpuId in candidates)
        {
            JObject body = BuildCreateBody(gpuId, dataCenterId);
            Logs.Info($"[RunPodPods] Creating pod '{plan.PodName}' on '{gpuId}'{(string.IsNullOrWhiteSpace(dataCenterId) ? "" : $" in {dataCenterId}")}...");
            try
            {
                cancel.ThrowIfCancellationRequested();
                JObject created = await ApiAsync(HttpMethod.Post, "/pods", body, CancellationToken.None) as JObject;
                string id = created?["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new SwarmReadableErrorException($"RunPod accepted the pod creation but returned no ID. Response: {created}");
                }
                Logs.Info($"[RunPodPods] Created pod '{id}' on '{gpuId}'. It bills until stopped or terminated.");
                return id;
            }
            catch (CloudApiException ex) when (ex.Status == 400 || ex.Status == 403)
            {
                lastDetail = ex.Detail;
                Logs.Debug($"[RunPodPods] '{gpuId}' unavailable or rejected ({ex.Status}): {ex.Detail}");
            }
        }
        throw new SwarmReadableErrorException($"Could not create a RunPod pod on any candidate GPU ({string.Join(", ", candidates)}). Last reason: {lastDetail ?? "unknown"}. If this repeats for every GPU it usually means the request itself is wrong rather than capacity being short.");
    }

    /// <summary>Builds the create body. Only known v2 fields are sent, since the API rejects unknown ones.
    /// A CUDA 13 image sets <c>minCudaVersion</c> on the gpu object (a top-level field is rejected) so the pod cannot land on a host whose driver cannot load the backend's kernels.
    /// The worker config tells the worker its token, where the volume (and so the models) is mounted, and the port its gateway listens on. It is the pod's only env entry.</summary>
    JObject BuildCreateBody(string gpuTypeId, string dataCenterId)
    {
        JObject body = new()
        {
            ["name"] = plan.PodName,
            ["cloud"] = string.IsNullOrWhiteSpace(plan.CloudType) ? "SECURE" : plan.CloudType.ToUpperFast(),
            ["disk"] = Math.Max(1, plan.ContainerDiskGb),
            ["ports"] = new JArray($"{plan.SwarmUIPort}/http", "22/tcp"),
            ["gpu"] = new JObject { ["id"] = gpuTypeId, ["count"] = Math.Max(1, plan.GpuCount) }
        };
        if (!string.IsNullOrWhiteSpace(plan.TemplateId))
        {
            body["templateId"] = plan.TemplateId;
        }
        else
        {
            body["image"] = plan.ImageName;
            if (CloudBackendTypes.NeedsCuda13(plan.ImageName))
            {
                ((JObject)body["gpu"])["minCudaVersion"] = "13.0";
            }
        }
        if (!string.IsNullOrWhiteSpace(dataCenterId))
        {
            body["dataCenterIds"] = new JArray(dataCenterId);
        }
        if (!string.IsNullOrWhiteSpace(plan.NetworkVolumeId))
        {
            body["mounts"] = new JObject
            {
                ["network"] = new JArray(new JObject { ["volumeId"] = plan.NetworkVolumeId, ["path"] = plan.VolumeMountPath })
            };
        }
        else if (plan.VolumeGb > 0)
        {
            body["mounts"] = new JObject
            {
                ["persistent"] = new JObject { ["size"] = Math.Max(10, plan.VolumeGb), ["path"] = plan.VolumeMountPath }
            };
        }
        CloudWorkerConfig workerConfig = new(plan.WorkerToken, plan.SwarmUIPort, plan.VolumeMountPath);
        body["env"] = new JObject { [CloudWorkerConfig.TransportKey] = workerConfig.Encode() };
        return body;
    }

    /// <summary>GPU types to attempt, in order. An explicit choice is used alone; otherwise the catalog is asked
    /// for types that are actually available for pods (in <paramref name="dataCenterId"/>, when the pod is pinned to one), cheapest first.</summary>
    public async Task<List<string>> GpuCandidatesAsync(string dataCenterId, CancellationToken cancel = default)
    {
        if (!string.IsNullOrWhiteSpace(plan.GpuTypeId))
        {
            return [plan.GpuTypeId];
        }
        string cloud = string.IsNullOrWhiteSpace(plan.CloudType) ? "SECURE" : plan.CloudType.ToUpperFast();
        JToken catalog = await ApiAsync(HttpMethod.Get, $"/catalog/gpus?include=AVAILABILITY&product=POD&cloud={cloud}", null, cancel);
        List<string> ordered = OrderGpuCandidates(catalog?["gpus"] as JArray, cloud, dataCenterId);
        if (ordered.Count == 0)
        {
            string where = string.IsNullOrWhiteSpace(dataCenterId) ? "" : $" in {dataCenterId} (where the network volume is)";
            throw new SwarmReadableErrorException($"RunPod reports no GPU types available for pods on the {cloud} cloud{where} right now. Try again later, set 'GpuTypeId' explicitly, or try the other cloud type.");
        }
        Logs.Info($"[RunPodPods] No GPU type set; trying available types{(string.IsNullOrWhiteSpace(dataCenterId) ? "" : $" in {dataCenterId}")} cheapest first: {string.Join(", ", ordered)}");
        return ordered;
    }

    /// <summary>The five cheapest GPU types in a catalog listing that are available, and when <paramref name="dataCenterId"/> is set,
    /// available in that data center. A pinned pod (a network volume pins it) can only be placed there, so GPUs elsewhere would only be refused.</summary>
    internal static List<string> OrderGpuCandidates(JArray gpus, string cloud, string dataCenterId)
    {
        List<(string Id, double Price)> usable = [];
        foreach (JToken t in gpus ?? [])
        {
            if (t is not JObject gpu)
            {
                continue;
            }
            string availability = gpu["availability"]?.ToString();
            if (string.Equals(availability, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(dataCenterId) && !(gpu["dataCenters"] as JArray ?? []).Any(d => string.Equals(d["id"]?.ToString(), dataCenterId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string id = gpu["id"]?.ToString();
            double price = gpu["price"]?[cloud.ToLowerFast()]?.Value<double>() ?? double.MaxValue;
            if (!string.IsNullOrWhiteSpace(id))
            {
                usable.Add((id, price));
            }
        }
        return [.. usable.OrderBy(g => g.Price).Select(g => g.Id).Take(5)];
    }

    /// <summary>Gathers what the settings form offers as choices: available GPU types with hourly price (cheapest first), pods, network volumes, pod-capable templates and data centers.
    /// A section that fails comes back empty, so one unavailable catalog does not blank the form. A volume's data center is read from v2's 'dataCenter' or v1's 'dataCenterId'.</summary>
    public async Task<JObject> ListAccountOptionsAsync(string cloud, CancellationToken cancel = default)
    {
        JArray gpus = [];
        try
        {
            JToken catalog = await ApiAsync(HttpMethod.Get, $"/catalog/gpus?include=AVAILABILITY&product=POD&cloud={cloud}", null, cancel);
            List<JObject> found = [];
            foreach (JToken t in catalog?["gpus"] as JArray ?? [])
            {
                if (t is not JObject gpu)
                {
                    continue;
                }
                string availability = gpu["availability"]?.ToString() ?? "UNKNOWN";
                if (string.Equals(availability, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                double? price = gpu["price"]?[cloud.ToLowerFast()]?.Value<double>();
                int? maxCount = gpu["maxCount"]?[cloud.ToLowerFast()]?.Value<int>();
                found.Add(new JObject
                {
                    ["id"] = gpu["id"]?.ToString(),
                    ["name"] = gpu["name"]?.ToString(),
                    ["memory_gb"] = gpu["memory"]?.Value<int>(),
                    ["price_per_hr"] = price,
                    ["max_count"] = maxCount,
                    ["availability"] = availability,
                    ["data_centers"] = new JArray((gpu["dataCenters"] as JArray ?? []).Select(d => d["id"]?.ToString()).Where(d => d is not null))
                });
            }
            gpus = new JArray(found.OrderBy(g => g["price_per_hr"]?.Value<double>() ?? double.MaxValue));
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodPods] Could not list GPU types: {ex.Message}");
        }
        JArray volumes = [];
        try
        {
            JToken vols = await ApiAsync(HttpMethod.Get, "/network-volumes", null, cancel);
            foreach (JToken t in vols?["networkVolumes"] as JArray ?? vols as JArray ?? [])
            {
                if (t is not JObject v)
                {
                    continue;
                }
                volumes.Add(new JObject
                {
                    ["id"] = v["id"]?.ToString(),
                    ["name"] = v["name"]?.ToString(),
                    ["size_gb"] = v["size"]?.Value<int>(),
                    ["data_center"] = (v["dataCenter"] ?? v["dataCenterId"])?.ToString()
                });
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodPods] Could not list network volumes: {ex.Message}");
        }
        JArray templates = [];
        try
        {
            JToken tpls = await ApiAsync(HttpMethod.Get, "/templates", null, cancel);
            foreach (JToken t in tpls?["templates"] as JArray ?? tpls as JArray ?? [])
            {
                if (t is not JObject tpl || (tpl["serverless"] ?? tpl["isServerless"])?.Type == JTokenType.Boolean && (tpl["serverless"] ?? tpl["isServerless"]).Value<bool>())
                {
                    continue;
                }
                templates.Add(new JObject
                {
                    ["id"] = tpl["id"]?.ToString(),
                    ["name"] = tpl["name"]?.ToString(),
                    ["image"] = tpl["image"]?.ToString() ?? tpl["imageName"]?.ToString()
                });
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodPods] Could not list templates: {ex.Message}");
        }
        JArray dataCenters = [];
        try
        {
            JToken dcs = await ApiAsync(HttpMethod.Get, "/catalog/datacenters", null, cancel);
            foreach (JToken t in dcs?["dataCenters"] as JArray ?? dcs as JArray ?? [])
            {
                if (t is not JObject dc)
                {
                    continue;
                }
                dataCenters.Add(new JObject { ["id"] = dc["id"]?.ToString(), ["name"] = dc["name"]?.ToString() });
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodPods] Could not list data centers: {ex.Message}");
        }
        JArray pods = [];
        try
        {
            foreach (JToken t in await ListPodsAsync(cancel))
            {
                if (t is not JObject p)
                {
                    continue;
                }
                pods.Add(new JObject
                {
                    ["id"] = p["id"]?.ToString(),
                    ["name"] = p["name"]?.ToString(),
                    ["status"] = p["status"]?.ToString(),
                    ["gpu"] = (p["gpu"] as JObject)?["id"]?.ToString(),
                    ["cost_per_hr"] = p["cost"]?.Value<double>()
                });
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RunPodPods] Could not list pods: {ex.Message}");
        }
        return new JObject
        {
            ["cloud"] = cloud,
            ["gpus"] = gpus,
            ["network_volumes"] = volumes,
            ["templates"] = templates,
            ["data_centers"] = dataCenters,
            ["pods"] = pods
        };
    }

    /// <summary>Finds the data center a network volume lives in, so the pod can be placed alongside it.</summary>
    public async Task<string> LookupVolumeDataCenterAsync(string volumeId, CancellationToken cancel = default)
    {
        try
        {
            JToken volumes = await ApiAsync(HttpMethod.Get, "/network-volumes", null, cancel);
            foreach (JToken t in volumes?["networkVolumes"] as JArray ?? volumes as JArray ?? [])
            {
                if (t is JObject v && string.Equals(v["id"]?.ToString(), volumeId, StringComparison.OrdinalIgnoreCase))
                {
                    return (v["dataCenter"] ?? v["dataCenterId"])?.ToString();
                }
            }
            Logs.Warning($"[RunPodPods] Network volume '{volumeId}' was not found on this account.");
        }
        catch (Exception ex)
        {
            Logs.Verbose($"[RunPodPods] Could not look up network volume data center: {ex.Message}");
        }
        return null;
    }

    /// <summary>Gets a pod by ID, or null if it does not exist.</summary>
    public async Task<JObject> GetPodAsync(string podId, CancellationToken cancel = default)
    {
        return await ApiAsync(HttpMethod.Get, $"/pods/{podId}", null, cancel, allowNotFound: true) as JObject;
    }

    /// <summary>Lists all pods on the account.</summary>
    public async Task<JArray> ListPodsAsync(CancellationToken cancel = default)
    {
        JToken result = await ApiAsync(HttpMethod.Get, "/pods", null, cancel);
        return result?["pods"] as JArray ?? result as JArray ?? [];
    }

    /// <summary>Stops the pod, releasing compute but keeping its disk so it can be started again.</summary>
    public async Task StopPodAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return;
        }
        Logs.Info($"[RunPodPods] Stopping pod '{ActiveInstanceId}'...");
        try
        {
            await ApiAsync(HttpMethod.Post, $"/pods/{ActiveInstanceId}/action", new JObject { ["action"] = "stop" }, cancel, allowNotFound: true);
        }
        catch (CloudApiException ex) when (ex.Status == 409)
        {
            Logs.Verbose($"[RunPodPods] Pod '{ActiveInstanceId}' cannot be stopped from its current state: {ex.Detail}");
        }
    }

    /// <summary>Permanently destroys the pod and its container disk. A network volume is only detached.</summary>
    public async Task TerminatePodAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return;
        }
        Logs.Info($"[RunPodPods] Terminating pod '{ActiveInstanceId}'...");
        await ApiAsync(HttpMethod.Delete, $"/pods/{ActiveInstanceId}", null, cancel, allowNotFound: true);
    }

    /// <summary>Calls the RunPod v2 REST API via the shared client (see <see cref="CloudApiClient.ApiAsync"/>).</summary>
    public Task<JToken> ApiAsync(HttpMethod method, string path, JObject body, CancellationToken cancel = default, bool allowNotFound = false)
    {
        return Api.ApiAsync(method, path, body, cancel, allowNotFound);
    }

    /// <summary>Null for an empty string.</summary>
    static string NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Name prefix of every pod this extension creates, used to recognise its orphans.</summary>
    public const string ManagedNamePrefix = "swarmui-cloudbackends";

    /// <summary>Running pods on the account that this extension created (by default name, or by an ID it remembers), as <c>{id, name, status, cost_per_hr}</c>.</summary>
    public async Task<List<JObject>> ListManagedRunningAsync(ICollection<string> knownIds, CancellationToken cancel = default)
    {
        List<JObject> result = [];
        foreach (JObject pod in (await ListPodsAsync(cancel)).OfType<JObject>())
        {
            string name = pod["name"]?.ToString() ?? "";
            string status = pod["status"]?.ToString() ?? "";
            if ((name.StartsWith(ManagedNamePrefix, StringComparison.OrdinalIgnoreCase) || knownIds.Contains(pod["id"]?.ToString() ?? "")) && status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new JObject { ["id"] = pod["id"], ["name"] = name, ["status"] = status, ["cost_per_hr"] = pod["cost"] });
            }
        }
        return result;
    }

    /// <summary>Stops any pod on the account by ID (orphan cleanup). Keeps its disk.</summary>
    public async Task StopPodByIdAsync(string podId, CancellationToken cancel = default)
    {
        Logs.Info($"[RunPodPods] Stopping pod '{podId}' on request...");
        await ApiAsync(HttpMethod.Post, $"/pods/{podId}/action", new JObject { ["action"] = "stop" }, cancel, allowNotFound: true);
    }
}
