using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>Options controlling how <see cref="VastAIInstanceProvider"/> finds or creates its instance.</summary>
public class VastAIInstancePlan
{
    /// <summary>Existing Vast.ai instance ID to use. If blank, the provider finds or creates one by <see cref="Label"/>.</summary>
    public string InstanceId = "";

    /// <summary>Port SwarmUI listens on inside the instance. Declared to Vast via a Docker -p flag in `env`.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Whether the provider may create an instance when no existing one is found.</summary>
    public bool AutoCreate = false;

    /// <summary>Label given to instances this backend creates, and used to find one again so a restart reuses it.</summary>
    public string Label = "swarmui-cloudbackends";

    public string Image = "";
    public string TemplateHashId = "";

    /// <summary>Offer to create from. Blank picks the cheapest matching on-demand offer at create time.</summary>
    public string OfferId = "";

    public int DiskGb = 20;
    public string NetworkVolumeId = "";
    public string VolumeMountPath = "/workspace";

    /// <summary>Extra environment variables, KEY=VALUE per line/comma. The SwarmUI port mapping is added automatically.</summary>
    public string Env = "";

    /// <summary>If true the instance is destroyed on shutdown; if false it is only stopped, so it can resume.</summary>
    public bool TerminateOnShutdown = false;
}

/// <summary>
/// <see cref="ICloudInstanceProvider"/> for Vast.ai rented instances, built on the console REST API at
/// https://console.vast.ai. Verified against the official `vastai` Python SDK/CLI source
/// (`vastai/api/instances.py`, `vastai/api/offers.py`, `vastai/api/storage.py`,
/// `vastai/data/instance.py`), not just prose docs - see <see cref="VastAIInstanceStatus"/>.
///
/// Unlike RunPod, Vast has no proxy domain: a port is reached at {public_ipaddr}:{mapped_external_port}
/// over plain HTTP, and that external port is randomly assigned and only known after creation. Port
/// exposure itself is declared inside the `env` dict as a Docker -p flag key ("-p {port}:{port}": "1"),
/// not a structured ports field the way RunPod's create body has.
///
/// Wakeup flow:
///   1. Resolve an instance: an explicit instance ID, else a previously created one found by label, else create one.
///   2. Issue a start (harmless if it's already running).
///   3. Poll until public_ipaddr and the mapped port are both present, which is when networking is real.
///   4. Build http://{ip}:{port} and confirm SwarmUI itself answers there before trusting it.
///
/// Instances bill continuously while running, so there is no keepalive to hold; shutdown stops (or
/// destroys) the instance instead.
/// </summary>
public class VastAIInstanceProvider(string apiKey, VastAIInstancePlan plan) : ICloudInstanceProvider
{
    static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();
    const string ApiBase = "https://console.vast.ai";

    /// <summary>Instance this provider is bound to. Resolved on first wake.</summary>
    public string ActiveInstanceId { get; private set; } = plan.InstanceId?.Trim() ?? "";

    public string ProviderName => "Vast.ai Instances";
    public string ApiKeyType => "vastai_api";

    // ── Status cache ──────────────────────────────────────────────────────────
    // Same instance-scoped cache-with-expiry idiom as RunPodPodsProvider.GetStatusAsync.
    static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(5);
    readonly SemaphoreSlim StatusLock = new(1, 1);
    VastAIInstanceStatus CachedStatus;
    DateTime StatusCacheExpiry = DateTime.MinValue;

    /// <summary>Gets the instance's live status. Returns null if no instance has been resolved yet.</summary>
    public async Task<VastAIInstanceStatus> GetStatusAsync(bool forceRefresh = false, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId)) { return null; }
        if (!forceRefresh && CachedStatus is not null && DateTime.UtcNow < StatusCacheExpiry) { return CachedStatus; }
        await StatusLock.WaitAsync(cancel);
        try
        {
            if (!forceRefresh && CachedStatus is not null && DateTime.UtcNow < StatusCacheExpiry) { return CachedStatus; }
            JObject inst = await GetInstanceAsync(ActiveInstanceId, cancel);
            CachedStatus = VastAIInstanceStatus.FromInstance(inst, plan.SwarmUIPort);
            StatusCacheExpiry = DateTime.UtcNow.Add(StatusCacheTtl);
            return CachedStatus;
        }
        finally { StatusLock.Release(); }
    }

    // ── ICloudInstanceProvider ───────────────────────────────────────────────

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId) && !plan.AutoCreate)
        {
            throw new SwarmReadableErrorException("No Vast.ai instance ID is set and AutoCreate is off. Set 'InstanceId', or enable 'AutoCreate' with an image (or template) and an offer.");
        }
        if (plan.AutoCreate && string.IsNullOrWhiteSpace(plan.Image) && string.IsNullOrWhiteSpace(plan.TemplateHashId))
        {
            throw new SwarmReadableErrorException("AutoCreate is on but neither 'Image' nor 'TemplateHashId' is set, so there is nothing to create an instance from.");
        }
        // Same endpoint the existing Vast.ai Serverless provider already validates against successfully.
        await ApiAsync(HttpMethod.Get, "/api/v0/users/current/", null, cancel);
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId) && await GetInstanceAsync(ActiveInstanceId, cancel) is null)
        {
            throw new SwarmReadableErrorException($"Vast.ai instance '{ActiveInstanceId}' was not found on this account. Check the InstanceId backend setting.");
        }
    }

    public async Task<CloudInstanceInfo> StartInstanceAsync(int maxWaitSeconds, int pollIntervalMs, CancellationToken cancel = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string instanceId = await ResolveInstanceAsync(cancel);
        JObject inst = await GetInstanceAsync(instanceId, cancel) ?? throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' no longer exists.");
        Logs.Info($"[VastAI Instances] Instance '{instanceId}' status={inst["actual_status"]}");
        // Vast has no documented "illegal from this state" action list the way RunPod publishes
        // actions[], so just always issue the start and tolerate a benign 4xx (already running).
        try { await ApiAsync(HttpMethod.Put, $"/api/v0/instances/{instanceId}/", new JObject { ["state"] = "running" }, cancel); }
        catch (VastApiException ex) when (ex.Status is 400 or 409) { Logs.Verbose($"[VastAI Instances] Start on '{instanceId}' returned {ex.Status} (likely already running): {ex.Detail}"); }
        int clampedPollMs = Math.Clamp(pollIntervalMs, 2000, 15000);
        VastAIInstanceStatus liveStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                liveStatus = await GetStatusAsync(forceRefresh: true, cancel);
                if (liveStatus?.PublicUrl is not null)
                {
                    Logs.Info($"[VastAI Instances] Instance '{instanceId}' networking is up at {liveStatus.PublicUrl}.");
                    break;
                }
                Logs.Verbose($"[VastAI Instances] Instance '{instanceId}' status={liveStatus?.Status}, waiting for public networking...");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logs.Verbose($"[VastAI Instances] Instance poll error: {ex.Message}"); }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (liveStatus?.PublicUrl is null)
        {
            throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' did not get public networking (an IP and a mapped port {plan.SwarmUIPort}) within {maxWaitSeconds}s.");
        }
        // Wait for SwarmUI itself, not just the port mapping: the mapped port answers as soon as the
        // container's network namespace exists, well before whatever is inside it has started listening.
        await WaitForSwarmAsync(liveStatus.PublicUrl, deadline, cancel);
        Logs.Info($"[VastAI Instances] SwarmUI is up on instance '{instanceId}' at {liveStatus.PublicUrl}");
        return new CloudInstanceInfo
        {
            PublicUrl = liveStatus.PublicUrl,
            InstanceId = instanceId,
            Description = string.IsNullOrWhiteSpace(liveStatus.GpuName) ? "Vast.ai instance" : $"{Math.Max(1, liveStatus.GpuCount)}x {liveStatus.GpuName}"
        };
    }

    /// <summary>Releases the instance, stopping or destroying it per the plan.</summary>
    public async Task ReleaseInstanceAsync(CancellationToken cancel = default)
    {
        if (plan.TerminateOnShutdown) { await DestroyInstanceAsync(cancel); }
        else { await StopInstanceRawAsync(cancel); }
    }

    /// <inheritdoc/>
    public async Task<double?> GetCostPerHourAsync(CancellationToken cancel = default)
    {
        VastAIInstanceStatus status = await GetStatusAsync(forceRefresh: false, cancel);
        return status?.CostPerHour;
    }

    public void Dispose() { }

    // ── Instance lifecycle ────────────────────────────────────────────────────

    /// <summary>Finds the instance to use: an explicit ID, else a previously created one by label, else creates one.</summary>
    async Task<string> ResolveInstanceAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId)) { return ActiveInstanceId; }
        if (!plan.AutoCreate) { throw new SwarmReadableErrorException("No Vast.ai instance ID is set and AutoCreate is off."); }
        // Reuse before creating: otherwise every restart would leave another instance behind, billing.
        foreach (JObject row in await ListInstancesAsync(cancel))
        {
            if (string.Equals(row["label"]?.ToString(), plan.Label, StringComparison.OrdinalIgnoreCase))
            {
                ActiveInstanceId = row["id"]?.ToString();
                Logs.Info($"[VastAI Instances] Reusing existing instance '{ActiveInstanceId}' labeled '{plan.Label}'.");
                return ActiveInstanceId;
            }
        }
        ActiveInstanceId = await CreateInstanceAsync(cancel);
        return ActiveInstanceId;
    }

    /// <summary>Creates an instance from the configured offer (or the cheapest matching one if none is set).</summary>
    public async Task<string> CreateInstanceAsync(CancellationToken cancel = default)
    {
        string offerId = plan.OfferId?.Trim();
        if (string.IsNullOrWhiteSpace(offerId))
        {
            JArray offers = await SearchOffersAsync(cancel);
            offerId = offers.FirstOrDefault()?["id"]?.ToString()
                ?? throw new SwarmReadableErrorException("No 'OfferId' set and no offers matched the default search. Pick one from the live dropdown, or check your account's region/verification filters.");
        }
        // Port exposure is a Docker -p flag encoded as an env dict key (Vast has no structured ports
        // field on create), so it's merged into the same object as any user-supplied env vars.
        JObject env = ParseEnv(plan.Env);
        env[$"-p {plan.SwarmUIPort}:{plan.SwarmUIPort}"] = "1";
        JObject body = new()
        {
            ["client_id"] = "me",
            ["disk"] = Math.Max(1, plan.DiskGb),
            ["env"] = env
        };
        if (!string.IsNullOrWhiteSpace(plan.TemplateHashId)) { body["template_hash_id"] = plan.TemplateHashId; }
        if (!string.IsNullOrWhiteSpace(plan.Image)) { body["image"] = plan.Image; }
        if (!string.IsNullOrWhiteSpace(plan.Label)) { body["label"] = plan.Label; }
        if (!string.IsNullOrWhiteSpace(plan.NetworkVolumeId))
        {
            body["volume_info"] = new JObject
            {
                ["create_new"] = false,
                ["volume_id"] = plan.NetworkVolumeId,
                ["mount_path"] = string.IsNullOrWhiteSpace(plan.VolumeMountPath) ? "/workspace" : plan.VolumeMountPath
            };
        }
        Logs.Info($"[VastAI Instances] Creating instance '{plan.Label}' from offer '{offerId}'...");
        JObject created = await ApiAsync(HttpMethod.Put, $"/api/v0/asks/{offerId}/", body, cancel) as JObject;
        if (created?["success"]?.Value<bool>() is not true)
        {
            throw new SwarmReadableErrorException($"Vast.ai rejected the instance creation: {created}");
        }
        string id = created["new_contract"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SwarmReadableErrorException($"Vast.ai accepted the instance creation but returned no ID. Response: {created}");
        }
        Logs.Info($"[VastAI Instances] Created instance '{id}' from offer '{offerId}'. It bills until stopped or destroyed.");
        return id;
    }

    /// <summary>Searches on-demand offers, cheapest first, filtered to rentable/verified/non-external listings.</summary>
    public async Task<JArray> SearchOffersAsync(CancellationToken cancel = default)
    {
        JObject query = new()
        {
            ["verified"] = new JObject { ["eq"] = true },
            ["external"] = new JObject { ["eq"] = false },
            ["rentable"] = new JObject { ["eq"] = true },
            ["rented"] = new JObject { ["eq"] = false },
            ["type"] = "on-demand",
            ["order"] = new JArray(new JArray("dph_total", "asc")),
            ["allocated_storage"] = Math.Max(1, plan.DiskGb)
        };
        JObject resp = await ApiAsync(HttpMethod.Post, "/api/v0/bundles/", query, cancel) as JObject;
        return resp?["offers"] as JArray ?? [];
    }

    /// <summary>
    /// Gathers everything the settings form needs to offer real choices: rentable offers (GPU, price,
    /// location) and the account's own network volumes. Individual sections degrade to empty rather
    /// than failing the whole call, matching RunPodPodsProvider.ListAccountOptionsAsync.
    /// </summary>
    public async Task<JObject> ListAccountOptionsAsync(CancellationToken cancel = default)
    {
        JArray offers = [];
        try { offers = await SearchOffersAsync(cancel); }
        catch (Exception ex) { Logs.Warning($"[VastAI Instances] Could not list offers: {ex.Message}"); }
        JArray volumes = [];
        try
        {
            JToken resp = await ApiAsync(HttpMethod.Get, "/api/v0/volumes?owner=me&type=network_volume", null, cancel);
            foreach (JToken t in resp?["volumes"] as JArray ?? [])
            {
                if (t is not JObject v) { continue; }
                volumes.Add(new JObject { ["id"] = v["id"]?.ToString(), ["name"] = v["name"]?.ToString(), ["size_gb"] = v["size"]?.Value<int>() });
            }
        }
        catch (Exception ex) { Logs.Warning($"[VastAI Instances] Could not list network volumes: {ex.Message}"); }
        return new JObject
        {
            ["success"] = true,
            ["offers"] = new JArray(offers.Select(o => new JObject
            {
                ["id"] = o["id"]?.ToString(),
                ["gpu_name"] = o["gpu_name"]?.ToString(),
                ["num_gpus"] = o["num_gpus"]?.Value<int>(),
                ["gpu_ram"] = o["gpu_ram"]?.Value<double>(),
                ["disk_space"] = o["disk_space"]?.Value<double>(),
                ["geolocation"] = o["geolocation"]?.ToString(),
                ["reliability"] = o["reliability"]?.Value<double>(),
                ["dph_total"] = o["dph_total"]?.Value<double>()
            })),
            ["network_volumes"] = volumes
        };
    }

    /// <summary>Lists the account's instances (one page - plenty for a personal account's find-by-label use).</summary>
    public async Task<JArray> ListInstancesAsync(CancellationToken cancel = default)
    {
        // The v1 list endpoint takes its filters as JSON-encoded query PARAMETERS, not a POST body -
        // confirmed against the official SDK's client.get("/api/v1/instances/", query_args=params).
        string selectFilters = Uri.EscapeDataString(new JObject().ToString(Newtonsoft.Json.Formatting.None));
        string orderBy = Uri.EscapeDataString(new JArray(new JObject { ["col"] = "id", ["dir"] = "asc" }).ToString(Newtonsoft.Json.Formatting.None));
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v1/instances/?select_filters={selectFilters}&order_by={orderBy}&limit=50", null, cancel);
        return [.. (resp?["instances"] as JArray ?? []).OfType<JObject>()];
    }

    /// <summary>Gets an instance by ID, or null if it does not exist.</summary>
    public async Task<JObject> GetInstanceAsync(string id, CancellationToken cancel = default)
    {
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v0/instances/{id}/?owner=me", null, cancel, allowNotFound: true);
        return resp?["instances"] as JObject;
    }

    /// <summary>Stops the instance, releasing compute but keeping its disk so it can be started again.</summary>
    public async Task StopInstanceRawAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId)) { return; }
        Logs.Info($"[VastAI Instances] Stopping instance '{ActiveInstanceId}'...");
        try { await ApiAsync(HttpMethod.Put, $"/api/v0/instances/{ActiveInstanceId}/", new JObject { ["state"] = "stopped" }, cancel, allowNotFound: true); }
        catch (VastApiException ex) { Logs.Verbose($"[VastAI Instances] Instance '{ActiveInstanceId}' stop returned {ex.Status}: {ex.Detail}"); }
    }

    /// <summary>Permanently destroys the instance and its container disk. A network volume is only detached.</summary>
    public async Task DestroyInstanceAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId)) { return; }
        Logs.Info($"[VastAI Instances] Destroying instance '{ActiveInstanceId}'...");
        await ApiAsync(HttpMethod.Delete, $"/api/v0/instances/{ActiveInstanceId}/", null, cancel, allowNotFound: true);
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

    // ── HTTP plumbing ─────────────────────────────────────────────────────────

    /// <summary>A Vast.ai API failure, carrying the raw response body.</summary>
    public class VastApiException(int status, string detail) : SwarmReadableErrorException($"Vast.ai API error {status}: {detail}")
    {
        public int Status = status;
        public string Detail = detail;
    }

    /// <summary>Calls the Vast.ai console REST API, translating failures into readable errors.</summary>
    public async Task<JToken> ApiAsync(HttpMethod method, string path, JObject body, CancellationToken cancel = default, bool allowNotFound = false)
    {
        using HttpRequestMessage request = new(method, $"{ApiBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null) { request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"); }
        Logs.Debug($"[VastAI Instances] {method} {path}");
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
        if (status == 401) { throw new SwarmReadableErrorException("Vast.ai API key was rejected (401). Check your key in User Settings -> API Keys."); }
        throw new VastApiException(status, text);
    }

    /// <summary>
    /// Waits until SwarmUI on the instance answers its API. Vast's port mapping answers as soon as the
    /// container's networking exists, well before anything inside it is actually listening, so this is
    /// the real readiness gate rather than the mapped-port check above.
    /// </summary>
    public async Task WaitForSwarmAsync(string publicUrl, DateTime deadline, CancellationToken cancel = default)
    {
        Exception last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject session = await Http.PostJson($"{publicUrl.TrimEnd('/')}/API/GetNewSession", [], null, cancel);
                if (!string.IsNullOrWhiteSpace(session?["session_id"]?.ToString())) { return; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            Logs.Verbose($"[VastAI Instances] Waiting for SwarmUI on {publicUrl} to answer...");
            await Task.Delay(5000, cancel);
        }
        throw new SwarmReadableErrorException($"Instance is up but SwarmUI at {publicUrl} did not answer before the startup timeout. Check that SwarmUI is installed and listening on port {plan.SwarmUIPort}, and that '-p {plan.SwarmUIPort}:{plan.SwarmUIPort}' actually applied (some Vast templates strip extra docker options).{(last is null ? "" : $" Last error: {last.Message}")}");
    }
}
