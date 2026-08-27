using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.RunPod;

/// <summary>
/// Options controlling how <see cref="RunPodPodsProvider"/> finds or creates its pod.
/// </summary>
public class RunPodPodPlan
{
    /// <summary>Existing pod ID to use. If blank, the provider finds or creates one by <see cref="PodName"/>.</summary>
    public string PodId = "";

    /// <summary>Port SwarmUI listens on inside the pod. Exposed as an http port so RunPod proxies it.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Whether the provider may create a pod when no existing one is found.</summary>
    public bool AutoCreate = false;

    /// <summary>Name used to find a previously auto-created pod, so restarts reuse it instead of creating another.</summary>
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

    /// <summary>If true, the pod is destroyed on backend shutdown. If false it is only stopped, so it can be resumed.</summary>
    public bool TerminateOnShutdown = false;
}

/// <summary>
/// <see cref="ICloudProvider"/> for RunPod on-demand GPU pods, built on the REST API at
/// https://rest.runpod.io/v1 (the GraphQL API this previously used is legacy).
///
/// Wakeup flow:
///   1. Resolve a pod: an explicit pod ID, else a previously auto-created pod found by name, else create one.
///   2. If the pod is not RUNNING, start it.
///   3. Poll until RunPod reports the pod running with its networking assigned.
///   4. Build the proxy URL https://{podId}-{port}.proxy.runpod.net and open a SwarmUI session on it.
///
/// Pods bill for as long as they are running, so unlike serverless there is no keepalive job. Shutdown
/// stops the pod by default (resumable, storage still billed) or terminates it if configured to.
/// </summary>
public class RunPodPodsProvider(string apiKey, RunPodPodPlan plan) : ICloudProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();
    const string ApiBase = "https://rest.runpod.io/v1";

    /// <summary>Pod this provider is currently bound to. Resolved on first wake.</summary>
    public string ActivePodId { get; private set; } = plan.PodId?.Trim() ?? "";

    public string ProviderName => "RunPod GPU Pods";
    public string ApiKeyType => "runpod_api";

    // ── ICloudProvider ────────────────────────────────────────────────────────

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActivePodId) && !plan.AutoCreate)
        {
            throw new SwarmReadableErrorException("No RunPod pod ID is set and Auto Create is off. Set 'PodId' in the backend settings, or enable 'AutoCreate' and set an image and GPU type.");
        }
        if (plan.AutoCreate && string.IsNullOrWhiteSpace(plan.ImageName) && string.IsNullOrWhiteSpace(plan.TemplateId))
        {
            throw new SwarmReadableErrorException("AutoCreate is enabled but neither 'ImageName' nor 'TemplateId' is set, so there is nothing to create a pod from.");
        }
        // Any authenticated call proves the key; listing pods is the cheapest and also warms the pod lookup.
        await ApiAsync(HttpMethod.Get, "/pods", null, cancel);
        if (!string.IsNullOrWhiteSpace(ActivePodId))
        {
            JObject pod = await GetPodAsync(ActivePodId, cancel);
            if (pod is null)
            {
                throw new SwarmReadableErrorException($"RunPod pod '{ActivePodId}' was not found on this account. Check the PodId backend setting.");
            }
        }
    }

    public async Task<CloudWorkerInfo> WakeupWorkerAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string podId = await ResolvePodAsync(cancel);
        JObject pod = await GetPodAsync(podId, cancel) ?? throw new SwarmReadableErrorException($"RunPod pod '{podId}' no longer exists.");
        string status = pod["desiredStatus"]?.ToString() ?? "";
        Logs.Info($"[RunPodPods] Pod '{podId}' desiredStatus={status}");
        if (status.Equals("TERMINATED", StringComparison.OrdinalIgnoreCase))
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' has been terminated and cannot be resumed. Create a new pod, or enable AutoCreate.");
        }
        if (!status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            Logs.Info($"[RunPodPods] Starting pod '{podId}'...");
            await ApiAsync(HttpMethod.Post, $"/pods/{podId}/start", null, cancel);
        }
        int clampedPollMs = Math.Clamp(pollIntervalMs, 2000, 15000);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                pod = await GetPodAsync(podId, cancel);
                if (pod is not null && (pod["desiredStatus"]?.ToString() ?? "").Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    // portMappings stays empty while the pod is still initializing, so it is a better
                    // "networking is live" signal than desiredStatus, which only reflects intent.
                    bool networkReady = pod["portMappings"] is JObject pm && pm.HasValues;
                    if (networkReady || pod["lastStartedAt"] is not null)
                    {
                        Logs.Info($"[RunPodPods] Pod '{podId}' is running.");
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logs.Verbose($"[RunPodPods] Pod poll error: {ex.Message}"); }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (DateTime.UtcNow >= deadline)
        {
            throw new SwarmReadableErrorException($"RunPod pod '{podId}' did not reach a running state within {maxWaitSeconds}s.");
        }
        string publicUrl = $"https://{podId}-{plan.SwarmUIPort}.proxy.runpod.net";
        string sessionId = await CreateSwarmSessionAsync(publicUrl, deadline, cancel);
        Logs.Info($"[RunPodPods] Connected to SwarmUI on pod '{podId}' at {publicUrl}");
        return new CloudWorkerInfo
        {
            PublicUrl = publicUrl,
            SessionId = sessionId,
            WorkerId = podId,
            Version = null
        };
    }

    /// <summary>
    /// Pods have no keepalive concept: they bill continuously until stopped, so there is nothing to
    /// hold open. Reported as established so the backend records a normal keepalive window.
    /// </summary>
    public Task<bool> StartKeepaliveAsync(CloudWorkerInfo worker, int durationSeconds, CancellationToken cancel = default)
    {
        return Task.FromResult(true);
    }

    public Task StopKeepaliveAsync() => Task.CompletedTask;

    public void Dispose() { }

    // ── Pod lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Finds the pod to use: an explicit ID, else a previous auto-created pod by name, else creates one.</summary>
    async Task<string> ResolvePodAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActivePodId))
        {
            return ActivePodId;
        }
        if (!plan.AutoCreate)
        {
            throw new SwarmReadableErrorException("No RunPod pod ID is set and AutoCreate is off.");
        }
        // Reuse before creating: without this, every SwarmUI restart would leave another billing pod behind.
        JArray pods = await ListPodsAsync(cancel);
        foreach (JToken t in pods)
        {
            if (t is JObject p && string.Equals(p["name"]?.ToString(), plan.PodName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p["desiredStatus"]?.ToString(), "TERMINATED", StringComparison.OrdinalIgnoreCase))
            {
                ActivePodId = p["id"]?.ToString();
                Logs.Info($"[RunPodPods] Reusing existing pod '{ActivePodId}' named '{plan.PodName}'.");
                return ActivePodId;
            }
        }
        ActivePodId = await CreatePodAsync(cancel);
        return ActivePodId;
    }

    /// <summary>Creates a pod per the configured plan. Returns the new pod ID.</summary>
    public async Task<string> CreatePodAsync(CancellationToken cancel = default)
    {
        JObject body = new()
        {
            ["name"] = plan.PodName,
            ["cloudType"] = string.IsNullOrWhiteSpace(plan.CloudType) ? "SECURE" : plan.CloudType.ToUpperInvariant(),
            ["computeType"] = "GPU",
            ["gpuCount"] = Math.Max(1, plan.GpuCount),
            ["containerDiskInGb"] = Math.Max(5, plan.ContainerDiskGb),
            // SwarmUI's port must be exposed as http so RunPod fronts it with its proxy domain.
            ["ports"] = new JArray($"{plan.SwarmUIPort}/http", "22/tcp")
        };
        if (!string.IsNullOrWhiteSpace(plan.ImageName)) { body["imageName"] = plan.ImageName; }
        if (!string.IsNullOrWhiteSpace(plan.TemplateId)) { body["templateId"] = plan.TemplateId; }
        if (!string.IsNullOrWhiteSpace(plan.GpuTypeId)) { body["gpuTypeIds"] = new JArray(plan.GpuTypeId); }
        if (!string.IsNullOrWhiteSpace(plan.NetworkVolumeId)) { body["networkVolumeId"] = plan.NetworkVolumeId; }
        else if (plan.VolumeGb > 0) { body["volumeInGb"] = plan.VolumeGb; }
        if (!string.IsNullOrWhiteSpace(plan.VolumeMountPath)) { body["volumeMountPath"] = plan.VolumeMountPath; }
        if (!string.IsNullOrWhiteSpace(plan.DataCenterId)) { body["dataCenterIds"] = new JArray(plan.DataCenterId); }
        JObject env = ParseEnv(plan.Env);
        if (env.HasValues) { body["env"] = env; }
        Logs.Info($"[RunPodPods] Creating pod '{plan.PodName}' (image: {plan.ImageName}, gpu: {plan.GpuTypeId} x{plan.GpuCount})...");
        JObject created = await ApiAsync(HttpMethod.Post, "/pods", body, cancel) as JObject;
        string id = created?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SwarmReadableErrorException($"RunPod pod creation did not return an ID. Response: {created}");
        }
        Logs.Info($"[RunPodPods] Created pod '{id}'. It will keep billing until stopped or terminated.");
        return id;
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
        // The API has returned both a bare array and an object wrapper across versions; accept either.
        return result as JArray ?? result?["pods"] as JArray ?? [];
    }

    /// <summary>Stops the pod. It can be started again later; storage keeps billing while stopped.</summary>
    public async Task StopPodAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActivePodId)) { return; }
        Logs.Info($"[RunPodPods] Stopping pod '{ActivePodId}'...");
        await ApiAsync(HttpMethod.Post, $"/pods/{ActivePodId}/stop", null, cancel, allowNotFound: true);
    }

    /// <summary>Permanently destroys the pod and its container disk.</summary>
    public async Task TerminatePodAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActivePodId)) { return; }
        Logs.Info($"[RunPodPods] Terminating pod '{ActivePodId}'...");
        await ApiAsync(HttpMethod.Delete, $"/pods/{ActivePodId}", null, cancel, allowNotFound: true);
    }

    /// <summary>Stops or terminates the pod according to the configured plan. Called on backend shutdown.</summary>
    public async Task ReleasePodAsync(CancellationToken cancel = default)
    {
        if (plan.TerminateOnShutdown) { await TerminatePodAsync(cancel); }
        else { await StopPodAsync(cancel); }
    }

    // ── HTTP plumbing ─────────────────────────────────────────────────────────

    /// <summary>Calls the RunPod REST API, translating failures into readable errors.</summary>
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
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SwarmReadableErrorException("RunPod API key was rejected (401). Check your key in User Settings -> API Keys.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"RunPod API {method} {path} failed ({(int)response.StatusCode}): {text}");
        }
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        try { return JToken.Parse(text); }
        catch (Exception) { return null; }
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
            Logs.Verbose($"[RunPodPods] Waiting for SwarmUI on {publicUrl} to answer...");
            await Task.Delay(5000, cancel);
        }
        throw new SwarmReadableErrorException($"Pod is running but SwarmUI at {publicUrl} did not answer before the startup timeout. Check that SwarmUI is installed in the pod and listening on port {plan.SwarmUIPort}.{(last is null ? "" : $" Last error: {last.Message}")}");
    }
}
