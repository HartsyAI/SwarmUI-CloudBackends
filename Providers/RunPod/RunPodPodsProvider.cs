using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>Options controlling how <see cref="RunPodPodsProvider"/> finds or creates its pod.</summary>
public class RunPodPodPlan
{
    /// <summary>Existing pod ID to use. If blank, the provider finds or creates one by <see cref="PodName"/>.</summary>
    public string PodId = "";

    /// <summary>Port SwarmUI listens on inside the pod. Declared as an http port so RunPod's proxy fronts it.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Whether the provider may create a pod when no existing one is found.</summary>
    public bool AutoCreate = false;

    /// <summary>Name given to created pods, and used to find one again so restarts do not pile up pods.</summary>
    public string PodName = "swarmui-cloudbackends";

    public string ImageName = "";
    public string GpuTypeId = "";
    public int GpuCount = 1;
    public int ContainerDiskGb = 50;
    public int VolumeGb = 0;
    public string VolumeMountPath = "/workspace";
    public string NetworkVolumeId = "";
    public string CloudType = "SECURE";
    public string DataCenterId = "";
    public string TemplateId = "";
    public string Env = "";

    /// <summary>If true the pod is destroyed on shutdown; if false it is only stopped, so it can resume.</summary>
    public bool TerminateOnShutdown = false;
}

/// <summary>
/// <see cref="ICloudProvider"/> for RunPod on-demand GPU pods, built on REST API v2 at
/// https://api.runpod.io/v2.
///
/// v2 is the current API: REST v1 (rest.runpod.io/v1) is deprecated and retires 2026-11-15, and the
/// GraphQL API retires in early 2027, so neither is a safe target for new code.
///
/// Wakeup flow:
///   1. Resolve a pod: an explicit pod ID, else a previously created pod found by name, else create one.
///   2. If the pod is not RUNNING, trigger the start action.
///   3. Poll until status is RUNNING with live runtime, which is when its networking is real.
///   4. Build https://{podId}-{port}.proxy.runpod.net and open a SwarmUI session on it.
///
/// Pods bill continuously while running, so there is no keepalive to hold; shutdown stops (or
/// terminates) the pod instead.
/// </summary>
public class RunPodPodsProvider(string apiKey, RunPodPodPlan plan) : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();
    const string ApiBase = "https://api.runpod.io/v2";

    /// <summary>Statuses a pod never leaves on its own.</summary>
    static readonly string[] TerminalStatuses = ["ERROR", "TERMINATED"];

    /// <summary>Pod this provider is bound to. Resolved on first wake.</summary>
    public string ActivePodId { get; private set; } = plan.PodId?.Trim() ?? "";

    public string ProviderName => "RunPod GPU Pods";
    public string ApiKeyType => "runpod_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActivePodId) && !plan.AutoCreate)
        {
            throw new SwarmReadableErrorException("No RunPod pod ID is set and AutoCreate is off. Set 'PodId', or enable 'AutoCreate' with an image (or template) and a GPU type.");
        }
        if (plan.AutoCreate && string.IsNullOrWhiteSpace(plan.ImageName) && string.IsNullOrWhiteSpace(plan.TemplateId))
        {
            throw new SwarmReadableErrorException("AutoCreate is on but neither 'ImageName' nor 'TemplateId' is set, so there is nothing to create a pod from.");
        }
        await ApiAsync(HttpMethod.Get, "/pods", null, cancel);
        if (!string.IsNullOrWhiteSpace(ActivePodId) && await GetPodAsync(ActivePodId, cancel) is null)
        {
            throw new SwarmReadableErrorException($"RunPod pod '{ActivePodId}' was not found on this account. Check the PodId backend setting.");
        }
    }

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string podId = await ResolvePodAsync(cancel);
        JObject pod = await GetPodAsync(podId, cancel) ?? throw new SwarmReadableErrorException($"RunPod pod '{podId}' no longer exists.");
        string status = pod["status"]?.ToString() ?? "";
        Logs.Info($"[RunPodPods] Pod '{podId}' status={status}");
        if (status.Equals("TERMINATED", StringComparison.OrdinalIgnoreCase))
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' has been terminated and cannot be resumed. Create a new pod, or enable AutoCreate.");
        }
        // v2 publishes the currently-legal transitions per pod, so ask rather than assume.
        if (!status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            if (PodAllows(pod, "start"))
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
                // runtime is null until the pod is actually RUNNING, so it is the honest readiness gate.
                if (status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase)
                    && pod["runtime"] is JObject runtime && (runtime["uptime"]?.Value<int>() ?? 0) > 0)
                {
                    Logs.Info($"[RunPodPods] Pod '{podId}' is running (uptime {runtime["uptime"]}s).");
                    break;
                }
                Logs.Verbose($"[RunPodPods] Pod '{podId}' status={status}, waiting for it to run...");
            }
            catch (SwarmReadableErrorException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logs.Verbose($"[RunPodPods] Pod poll error: {ex.Message}"); }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (DateTime.UtcNow >= deadline)
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' did not reach a running state within {maxWaitSeconds}s.");
        }
        // The API never returns a proxy URL; it is constructed from the pod id and the internal port.
        string publicUrl = $"https://{podId}-{plan.SwarmUIPort}.proxy.runpod.net";
        string sessionId = await CreateSwarmSessionAsync(publicUrl, deadline, cancel);
        Logs.Info($"[RunPodPods] Connected to SwarmUI on pod '{podId}' at {publicUrl}");
        return new CloudWorkerInfo { PublicUrl = publicUrl, SessionId = sessionId, WorkerId = podId, Version = null };
    }

    /// <summary>True if the pod's published action list permits the given action.</summary>
    static bool PodAllows(JObject pod, string action)
    {
        return pod["actions"] is JArray actions && actions.Any(a => string.Equals(a.ToString(), action, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Pods have no keepalive concept: they bill until stopped, so there is nothing to hold open.</summary>
    public Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default) => Task.FromResult(true);

    public Task StopKeepaliveAsync() => Task.CompletedTask;

    public void Dispose() { }

    // ── Pod lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Finds the pod to use: an explicit ID, else a previously created pod by name, else creates one.</summary>
    async Task<string> ResolvePodAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActivePodId)) { return ActivePodId; }
        if (!plan.AutoCreate)
        {
            throw new SwarmReadableErrorException("No RunPod pod ID is set and AutoCreate is off.");
        }
        // Reuse before creating: otherwise every restart would leave another pod behind, billing.
        foreach (JToken t in await ListPodsAsync(cancel))
        {
            if (t is JObject p && string.Equals(p["name"]?.ToString(), plan.PodName, StringComparison.OrdinalIgnoreCase)
                && !TerminalStatuses.Contains(p["status"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                ActivePodId = p["id"]?.ToString();
                Logs.Info($"[RunPodPods] Reusing existing pod '{ActivePodId}' named '{plan.PodName}'.");
                return ActivePodId;
            }
        }
        ActivePodId = await CreatePodAsync(cancel);
        return ActivePodId;
    }

    /// <summary>
    /// Creates a pod. v2 places exactly one GPU type per call with no server-side fallback, so this
    /// follows RunPod's documented read-then-create pattern: order candidates from the catalog, then
    /// try them until one is accepted.
    /// </summary>
    public async Task<string> CreatePodAsync(CancellationToken cancel = default)
    {
        string dataCenterId = plan.DataCenterId;
        // A network volume can only attach to a pod in its own data center, so let the volume decide.
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
        List<string> candidates = await GpuCandidatesAsync(cancel);
        string lastDetail = null;
        foreach (string gpuId in candidates)
        {
            JObject body = BuildCreateBody(gpuId, dataCenterId);
            Logs.Info($"[RunPodPods] Creating pod '{plan.PodName}' on '{gpuId}'{(string.IsNullOrWhiteSpace(dataCenterId) ? "" : $" in {dataCenterId}")}...");
            try
            {
                JObject created = await ApiAsync(HttpMethod.Post, "/pods", body, cancel) as JObject;
                string id = created?["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new SwarmReadableErrorException($"RunPod accepted the pod creation but returned no ID. Response: {created}");
                }
                Logs.Info($"[RunPodPods] Created pod '{id}' on '{gpuId}'. It bills until stopped or terminated.");
                return id;
            }
            catch (RunPodApiException ex) when (ex.Status == 400 || ex.Status == 403)
            {
                // 400 covers both a bad request and no capacity, so move to the next candidate; if every
                // candidate fails the same way, the last detail is reported as the likely real cause.
                lastDetail = ex.Detail;
                Logs.Debug($"[RunPodPods] '{gpuId}' unavailable or rejected ({ex.Status}): {ex.Detail}");
            }
        }
        throw new SwarmReadableErrorException($"Could not create a RunPod pod on any candidate GPU ({string.Join(", ", candidates)}). Last reason: {lastDetail ?? "unknown"}. If this repeats for every GPU it usually means the request itself is wrong rather than capacity being short.");
    }

    /// <summary>Builds the create body. Only known v2 fields are sent, since the API rejects unknown ones.</summary>
    JObject BuildCreateBody(string gpuTypeId, string dataCenterId)
    {
        JObject body = new()
        {
            ["name"] = plan.PodName,
            ["cloud"] = string.IsNullOrWhiteSpace(plan.CloudType) ? "SECURE" : plan.CloudType.ToUpperInvariant(),
            ["disk"] = Math.Max(1, plan.ContainerDiskGb),
            // SwarmUI's port must be http for RunPod's proxy to route to it.
            ["ports"] = new JArray($"{plan.SwarmUIPort}/http", "22/tcp"),
            ["gpu"] = new JObject { ["id"] = gpuTypeId, ["count"] = Math.Max(1, plan.GpuCount) }
        };
        if (!string.IsNullOrWhiteSpace(plan.TemplateId)) { body["templateId"] = plan.TemplateId; }
        else { body["image"] = plan.ImageName; }
        if (!string.IsNullOrWhiteSpace(dataCenterId)) { body["dataCenterIds"] = new JArray(dataCenterId); }
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
        JObject env = ParseEnv(plan.Env);
        if (env.HasValues) { body["env"] = env; }
        return body;
    }

    /// <summary>
    /// GPU types to attempt, in order. An explicit choice is used alone; otherwise the catalog is asked
    /// for types that are actually available for pods, cheapest first.
    /// </summary>
    public async Task<List<string>> GpuCandidatesAsync(CancellationToken cancel = default)
    {
        if (!string.IsNullOrWhiteSpace(plan.GpuTypeId)) { return [plan.GpuTypeId]; }
        string cloud = string.IsNullOrWhiteSpace(plan.CloudType) ? "SECURE" : plan.CloudType.ToUpperInvariant();
        JToken catalog = await ApiAsync(HttpMethod.Get, $"/catalog/gpus?include=AVAILABILITY&product=POD&cloud={cloud}", null, cancel);
        List<(string Id, double Price)> usable = [];
        foreach (JToken t in catalog?["gpus"] as JArray ?? [])
        {
            if (t is not JObject gpu) { continue; }
            string availability = gpu["availability"]?.ToString();
            if (string.Equals(availability, "NONE", StringComparison.OrdinalIgnoreCase)) { continue; }
            string id = gpu["id"]?.ToString();
            double price = gpu["price"]?[cloud.ToLowerInvariant()]?.Value<double>() ?? double.MaxValue;
            if (!string.IsNullOrWhiteSpace(id)) { usable.Add((id, price)); }
        }
        if (usable.Count == 0)
        {
            throw new SwarmReadableErrorException($"RunPod reports no GPU types available for pods on the {cloud} cloud right now. Set 'GpuTypeId' explicitly, or try the other cloud type.");
        }
        List<string> ordered = [.. usable.OrderBy(g => g.Price).Select(g => g.Id).Take(5)];
        Logs.Info($"[RunPodPods] No GPU type set; trying available types cheapest first: {string.Join(", ", ordered)}");
        return ordered;
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
                    return v["dataCenterId"]?.ToString();
                }
            }
            Logs.Warning($"[RunPodPods] Network volume '{volumeId}' was not found on this account.");
        }
        catch (Exception ex) { Logs.Verbose($"[RunPodPods] Could not look up network volume data center: {ex.Message}"); }
        return null;
    }

    /// <summary>Parses newline or comma separated KEY=VALUE pairs into a JSON object.</summary>
    public static JObject ParseEnv(string raw)
    {
        JObject result = [];
        if (string.IsNullOrWhiteSpace(raw)) { return result; }
        foreach (string line in raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) { result[line[..eq].Trim()] = line[(eq + 1)..].Trim(); }
        }
        return result;
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
        if (string.IsNullOrWhiteSpace(ActivePodId)) { return; }
        Logs.Info($"[RunPodPods] Stopping pod '{ActivePodId}'...");
        try { await ApiAsync(HttpMethod.Post, $"/pods/{ActivePodId}/action", new JObject { ["action"] = "stop" }, cancel, allowNotFound: true); }
        catch (RunPodApiException ex) when (ex.Status == 409)
        {
            Logs.Verbose($"[RunPodPods] Pod '{ActivePodId}' cannot be stopped from its current state: {ex.Detail}");
        }
    }

    /// <summary>Permanently destroys the pod and its container disk. A network volume is only detached.</summary>
    public async Task TerminatePodAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActivePodId)) { return; }
        Logs.Info($"[RunPodPods] Terminating pod '{ActivePodId}'...");
        await ApiAsync(HttpMethod.Delete, $"/pods/{ActivePodId}", null, cancel, allowNotFound: true);
    }

    /// <summary>Stops or terminates the pod per the plan. Called on backend shutdown.</summary>
    public async Task ReleasePodAsync(CancellationToken cancel = default)
    {
        if (plan.TerminateOnShutdown) { await TerminatePodAsync(cancel); }
        else { await StopPodAsync(cancel); }
    }

    // ── HTTP plumbing ─────────────────────────────────────────────────────────

    /// <summary>A RunPod API failure, carrying the RFC 9457 problem details v2 returns.</summary>
    public class RunPodApiException(int status, string detail) : SwarmReadableErrorException($"RunPod API error {status}: {detail}")
    {
        public int Status = status;
        public string Detail = detail;
    }

    /// <summary>Calls the RunPod v2 REST API, translating failures into readable errors.</summary>
    public async Task<JToken> ApiAsync(HttpMethod method, string path, JObject body, CancellationToken cancel = default, bool allowNotFound = false)
    {
        using HttpRequestMessage request = new(method, $"{ApiBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        }
        Logs.Debug($"[RunPodPods] {method} {path}");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) { return null; }
        if (response.IsSuccessStatusCode)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            try { return JToken.Parse(text); }
            catch (Exception) { return null; }
        }
        int status = (int)response.StatusCode;
        string detail = text;
        try { detail = JObject.Parse(text)["detail"]?.ToString() ?? text; }
        catch (Exception) { }
        throw status switch
        {
            401 => new SwarmReadableErrorException("RunPod API key was rejected (401). Check your key in User Settings -> API Keys."),
            402 => new SwarmReadableErrorException("RunPod reports an insufficient account balance (402). Add credit before starting pods."),
            422 => new SwarmReadableErrorException($"RunPod rejected the request as invalid (422): {detail}. This is a bug in the request rather than a capacity problem."),
            429 => new SwarmReadableErrorException($"RunPod rate limited this request (429). Retry after {response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0}s."),
            _ => new RunPodApiException(status, detail)
        };
    }

    /// <summary>Opens a SwarmUI session on the pod, retrying while SwarmUI finishes booting.</summary>
    public async Task<string> CreateSwarmSessionAsync(string publicUrl, DateTime deadline, CancellationToken cancel = default)
    {
        Exception last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject session = await Http.PostJson($"{publicUrl.TrimEnd('/')}/API/GetNewSession", [], null, cancel);
                string id = session?["session_id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) { return id; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            // A 502 from the proxy is the normal answer while the container is still starting its service.
            Logs.Verbose($"[RunPodPods] Waiting for SwarmUI on {publicUrl} to answer...");
            await Task.Delay(5000, cancel);
        }
        throw new SwarmReadableErrorException($"Pod is running but SwarmUI at {publicUrl} did not answer before the startup timeout. Check that SwarmUI is installed in the pod and listening on port {plan.SwarmUIPort}, and that the port is exposed as http.{(last is null ? "" : $" Last error: {last.Message}")}");
    }
}
