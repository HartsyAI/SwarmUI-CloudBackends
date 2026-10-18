using Hartsy.Extensions.CloudBackends.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Sockets;

namespace Hartsy.Extensions.CloudBackends.Providers.VastAI;

/// <summary>Options controlling how <see cref="VastAIInstanceProvider"/> finds or creates its instance.</summary>
public class VastAIInstancePlan
{
    /// <summary>Existing Vast.ai instance ID to use. If blank, the provider finds or creates one by <see cref="Label"/>.</summary>
    public string InstanceId = "";

    /// <summary>Port SwarmUI listens on inside the instance. Declared to Vast via a Docker -p flag in `env`.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Instance ID remembered from a previous run, used as a reattach hint after live verification. Never blocks a fresh create when stale.</summary>
    public string PersistedId = "";

    /// <summary>Label given to instances this backend creates, and used to find one again so a restart reuses it.</summary>
    public string Label = "swarmui-cloudbackends";

    /// <summary>Docker image to create the instance from (ignored when a template is set).</summary>
    public string Image = "";
    /// <summary>Vast.ai template to create the instance from, instead of an image.</summary>
    public string TemplateHashId = "";

    /// <summary>GPU model to rent (Vast's gpu_name). Blank takes the cheapest single GPU with at least <see cref="VastAIInstanceProvider.DefaultMinGpuRamMb"/> of VRAM.</summary>
    public string GpuName = "";

    /// <summary>Container disk for a created instance, in GB.</summary>
    public int DiskGb = 20;
    /// <summary>Existing network volume to attach, if any.</summary>
    public string NetworkVolumeId = "";
    /// <summary>Where the volume is mounted inside the instance.</summary>
    public string VolumeMountPath = "/workspace";

    /// <summary>Extra environment variables, KEY=VALUE per line/comma. The SwarmUI port mapping is added automatically.</summary>
    public string Env = "";

    /// <summary>Gateway token for the instance's SwarmUI worker, injected as SWARMUI_WORKER_TOKEN when creating it.</summary>
    public string WorkerToken = "";

    /// <summary>Reach the instance over HTTPS with Vast's instance certificate (the Hartsy Vast worker serves it).</summary>
    public bool UseTls = true;

    /// <summary>If true, releasing the instance (Stop, a failsafe, or shutdown) destroys it; if false it is only stopped, so it can resume.</summary>
    public bool DeleteOnStop = true;
}

/// <summary>
/// <see cref="ICloudInstanceProvider"/> for Vast.ai rented instances, built on the console REST API at
/// https://console.vast.ai. Verified against the official `vastai` Python SDK/CLI source
/// (`vastai/api/instances.py`, `vastai/api/offers.py`, `vastai/api/storage.py`,
/// `vastai/data/instance.py`), not just prose docs - see <see cref="StatusFromInstance"/>.
///
/// Unlike RunPod, Vast has no proxy domain: a port is reached at {public_ipaddr}:{mapped_external_port},
/// and that external port is randomly assigned and only known after creation. Port
/// exposure itself is declared inside the `env` dict as a Docker -p flag key ("-p {port}:{port}": "1"),
/// not a structured ports field the way RunPod's create body has.
///
/// Wakeup flow:
///   1. Resolve an instance: an explicit instance ID, else a previously created one found by label, else create one.
///   2. Issue a start (harmless if it's already running).
///   3. Poll until public_ipaddr and the mapped port are both present, which is when networking is real.
///   4. Check the host forwards the port (else destroy a just-created instance and rent another machine), then confirm SwarmUI itself answers there.
///
/// Instances bill continuously while running, so there is no keepalive to hold; shutdown stops (or
/// destroys) the instance instead.
/// </summary>
public class VastAIInstanceProvider(string apiKey, VastAIInstancePlan plan) : ICloudInstanceProvider
{
    /// <summary>Instance this provider is bound to. Resolved on first wake.</summary>
    public string ActiveInstanceId { get; private set; } = plan.InstanceId?.Trim() ?? "";

    public string ProviderName => "Vast.ai Instances";

    /// <summary>Shared HTTP plumbing, with Vast's well-known failure statuses mapped to actionable messages.</summary>
    readonly CloudApiClient Api = new("VastAI Instances", "https://console.vast.ai", apiKey, (status, detail) => status switch
    {
        401 => new SwarmReadableErrorException("Vast.ai API key was rejected (401). Check your key in User Settings -> API Keys."),
        _ => null
    });

    /// <summary>Short-lived cache of the instance's status.</summary>
    readonly CloudStatusCache StatusCache = new();

    /// <summary>Fewest direct ports an offer's host may have.</summary>
    public const int MinDirectPorts = 64;

    /// <summary>How long a running instance's port may refuse to accept connections before its host is judged unreachable.</summary>
    public const int PortReachSeconds = 180;

    /// <summary>Hosts tried per start: a created instance on an unreachable host is destroyed and another machine rented.</summary>
    public const int MaxHostAttempts = 3;

    /// <summary>Instance created by the current start attempt, if any. Only those may be destroyed and replaced.</summary>
    string CreatedInstanceId;

    /// <summary>Machines whose hosts never forwarded the port, skipped when picking an offer.</summary>
    readonly HashSet<string> SkippedMachines = [];

    /// <summary>Gets the instance's live status. Returns null if no instance has been resolved yet.</summary>
    public async Task<CloudInstanceStatus> GetStatusAsync(bool forceRefresh = false, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return null;
        }
        return await StatusCache.GetAsync(forceRefresh, async () => StatusFromInstance(await GetInstanceAsync(ActiveInstanceId, cancel)), cancel);
    }

    /// <summary>
    /// Projects Vast's <c>GET /instances/{id}/</c> response into the provider-neutral status shape
    /// (field names verified against the official `vastai` SDK's <c>vastai/data/instance.py</c>).
    /// Vast has no documented status enum worth hardcoding against - the CLI prints `actual_status`
    /// raw - so it stays opaque/display-only; readiness is gated on connectivity instead (an IP plus
    /// the mapped port, then SwarmUI itself answering).
    /// </summary>
    CloudInstanceStatus StatusFromInstance(JObject inst)
    {
        if (inst is null)
        {
            return null;
        }
        string ip = inst["public_ipaddr"]?.ToString();
        int? mappedPort = null;
        // Vast's port map is Docker-inspect shaped: ports["7801/tcp"] = [{ "HostIp": ..., "HostPort": "..." }, ...]
        if (inst["ports"] is JObject ports && ports[$"{plan.SwarmUIPort}/tcp"] is JArray bindings && bindings.Count > 0)
        {
            string hostPort = bindings[0]?["HostPort"]?.ToString();
            if (int.TryParse(hostPort, out int parsed))
            {
                mappedPort = parsed;
            }
        }
        return new CloudInstanceStatus
        {
            InstanceId = inst["id"]?.ToString(),
            Status = inst["actual_status"]?.ToString() ?? "unknown",
            GpuName = inst["gpu_name"]?.ToString(),
            GpuCount = ((int?)inst["num_gpus"]) ?? 0,
            CostPerHour = ((double?)inst["dph_total"]) ?? 0,
            UptimeSeconds = (int)((((double?)inst["uptime_mins"]) ?? 0) * 60),
            // Vast has no proxy domain, just {public_ipaddr}:{mapped_external_port}; the worker serves HTTPS with its Vast-signed certificate.
            PublicUrl = (!string.IsNullOrWhiteSpace(ip) && mappedPort is not null) ? $"{(plan.UseTls ? "https" : "http")}://{ip}:{mappedPort}" : null
        };
    }

    // ── ICloudInstanceProvider ───────────────────────────────────────────────

    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId) && string.IsNullOrWhiteSpace(plan.Image) && string.IsNullOrWhiteSpace(plan.TemplateHashId))
        {
            throw new SwarmReadableErrorException("Set 'Image' or 'TemplateHashId' so an instance can be created, or set 'InstanceId' to use an existing instance.");
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
        for (int attempt = 1; ; attempt++)
        {
            CreatedInstanceId = null;
            CloudInstanceInfo started = await StartOnceAsync(maxWaitSeconds, pollIntervalMs, attempt < MaxHostAttempts, cancel);
            if (started is not null)
            {
                return started;
            }
        }
    }

    /// <summary>Starts (creating if needed) and waits for the instance. Returns null if it rented an unreachable host and destroyed it, so the caller can try another.</summary>
    async Task<CloudInstanceInfo> StartOnceAsync(int maxWaitSeconds, int pollIntervalMs, bool mayRedraw, CancellationToken cancel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string instanceId = await ResolveInstanceAsync(cancel);
        JObject inst = await GetInstanceAsync(instanceId, cancel) ?? throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' no longer exists.");
        Logs.Info($"[VastAI Instances] Instance '{instanceId}' status={inst["actual_status"]}");
        // Vast has no documented "illegal from this state" action list the way RunPod publishes
        // actions[], so just always issue the start and tolerate a benign 4xx (already running).
        try
        {
            await ApiAsync(HttpMethod.Put, $"/api/v0/instances/{instanceId}/", new JObject { ["state"] = "running" }, cancel);
        }
        catch (CloudApiException ex) when (ex.Status is 400 or 409)
        {
            Logs.Verbose($"[VastAI Instances] Start on '{instanceId}' returned {ex.Status} (likely already running): {ex.Detail}");
        }
        int clampedPollMs = Math.Clamp(pollIntervalMs, 2000, 15000);
        CloudInstanceStatus liveStatus = null;
        string lastState = null;
        // A restarted instance still reads "exited" until its host acts on the start request.
        DateTime stoppedIsFatalAfter = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject polled = await GetInstanceAsync(instanceId, cancel);
                liveStatus = StatusFromInstance(polled);
                // Vast's own progress text (image pull, errors), which says far more than the status word.
                string message = polled?["status_msg"]?.ToString()?.Trim();
                string state = $"{liveStatus?.Status}{(string.IsNullOrWhiteSpace(message) ? "" : $": {message}")}";
                if (state != lastState)
                {
                    Logs.Info($"[VastAI Instances] Instance '{instanceId}' {state}");
                    lastState = state;
                }
                if (liveStatus?.Status is "exited" or "offline" && DateTime.UtcNow > stoppedIsFatalAfter)
                {
                    throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' stopped while starting ({state}). Check the instance's logs in the Vast.ai console.");
                }
                // The address is assigned while the container is still loading (pulling the image), so wait for it to run.
                if (liveStatus?.PublicUrl is not null && liveStatus.Status == "running")
                {
                    Logs.Info($"[VastAI Instances] Instance '{instanceId}' is running at {liveStatus.PublicUrl}.");
                    break;
                }
            }
            catch (SwarmReadableErrorException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logs.Verbose($"[VastAI Instances] Instance poll error: {ex.Message}");
            }
            await Task.Delay(clampedPollMs, cancel);
        }
        if (liveStatus?.PublicUrl is null || liveStatus.Status != "running")
        {
            throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' was not running with public networking (an IP and a mapped port {plan.SwarmUIPort}) within {maxWaitSeconds}s{(lastState is null ? "" : $" (last: {lastState})")}.");
        }
        // Some hosts map the port but never forward traffic to it. The worker's gateway listens within seconds of the
        // container running, so a port that never accepts a connection means the host, not the worker.
        if (!await WaitForPortAsync(liveStatus.PublicUrl, Min(deadline, DateTime.UtcNow.AddSeconds(PortReachSeconds)), cancel))
        {
            string machineId = (await GetInstanceAsync(instanceId, cancel))?["machine_id"]?.ToString();
            if (!mayRedraw || CreatedInstanceId != instanceId)
            {
                throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' is running, but its port {liveStatus.PublicUrl} never accepted a connection, so that host's network does not reach it. Destroy the instance and start again to rent a different machine.");
            }
            Logs.Warning($"[VastAI Instances] Instance '{instanceId}' (machine {machineId}) never accepted a connection on {liveStatus.PublicUrl}; destroying it and renting a different machine.");
            if (!string.IsNullOrWhiteSpace(machineId))
            {
                SkippedMachines.Add(machineId);
            }
            await DestroyInstanceAsync(cancel);
            ActiveInstanceId = "";
            return null;
        }
        // Wait for SwarmUI itself, not just the port mapping: the mapped port answers as soon as the
        // container's network namespace exists, well before whatever is inside it has started listening.
        await Api.WaitForSwarmAsync(liveStatus.PublicUrl, deadline, $"Check that SwarmUI is installed and listening on port {plan.SwarmUIPort}, and that '-p {plan.SwarmUIPort}:{plan.SwarmUIPort}' actually applied (some Vast templates strip extra docker options).", cancel, string.IsNullOrWhiteSpace(plan.WorkerToken) ? null : plan.WorkerToken, plan.UseTls ? VastTls.Http : null);
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
        if (plan.DeleteOnStop)
        {
            await DestroyInstanceAsync(cancel);
        }
        else
        {
            await StopInstanceRawAsync(cancel);
        }
    }

    /// <inheritdoc/>
    public async Task<double?> GetCostPerHourAsync(CancellationToken cancel = default)
    {
        CloudInstanceStatus status = await GetStatusAsync(forceRefresh: false, cancel);
        return status?.CostPerHour;
    }

    public void Dispose() { }

    // ── Instance lifecycle ────────────────────────────────────────────────────

    /// <summary>Finds the instance to use: an explicit ID, else a previously created one by label, else creates one.</summary>
    async Task<string> ResolveInstanceAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return ActiveInstanceId;
        }
        // An instance remembered from a previous run is only a hint - verify it still exists before
        // trusting it, so a stale memory can never block or misdirect a fresh create.
        if (!string.IsNullOrWhiteSpace(plan.PersistedId))
        {
            if (await GetInstanceAsync(plan.PersistedId, cancel) is not null)
            {
                ActiveInstanceId = plan.PersistedId;
                Logs.Info($"[VastAI Instances] Reattaching remembered instance '{ActiveInstanceId}'.");
                return ActiveInstanceId;
            }
            Logs.Debug($"[VastAI Instances] Remembered instance '{plan.PersistedId}' is gone, ignoring it.");
        }
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
        CreatedInstanceId = ActiveInstanceId;
        return ActiveInstanceId;
    }

    /// <summary>Creates an instance from the configured offer (or the cheapest matching one if none is set).</summary>
    public async Task<string> CreateInstanceAsync(CancellationToken cancel = default)
    {
        string gpuName = plan.GpuName?.Trim() ?? "";
        // A specific offer is one machine's slot and goes stale within minutes, so the cheapest one is picked at create time.
        JArray offers = await SearchOffersAsync(gpuName, gpuName.Length > 0 ? 0 : DefaultMinGpuRamMb, cancel);
        string offerId = offers.FirstOrDefault(o => !SkippedMachines.Contains(o["machine_id"]?.ToString() ?? ""))?["id"]?.ToString()
            ?? throw new SwarmReadableErrorException(gpuName.Length > 0
                ? $"No Vast.ai machine with a {gpuName} is available to rent right now. Pick another GPU, or '(cheapest available)'."
                : "No Vast.ai machine matched the default search (one GPU with at least 16 GB of VRAM). Pick a GPU from the list, or try again later.");
        // Port exposure is a Docker -p flag encoded as an env dict key (Vast has no structured ports
        // field on create), so it's merged into the same object as any user-supplied env vars.
        JObject env = CloudApiClient.ParseEnv(plan.Env);
        // The worker's gateway listens on this port, so it matches the port opened below.
        env["SWARMUI_PUBLIC_PORT"] ??= $"{plan.SwarmUIPort}";
        if (!string.IsNullOrWhiteSpace(plan.WorkerToken))
        {
            env["SWARMUI_WORKER_TOKEN"] = plan.WorkerToken;
        }
        // Tells the worker image where the volume (and so the models) is mounted.
        if (env["VOLUME_PATH"] is null && !string.IsNullOrWhiteSpace(plan.VolumeMountPath))
        {
            env["VOLUME_PATH"] = plan.VolumeMountPath;
        }
        env[$"-p {plan.SwarmUIPort}:{plan.SwarmUIPort}"] = "1";
        JObject body = new()
        {
            ["client_id"] = "me",
            ["disk"] = Math.Max(1, plan.DiskGb),
            ["env"] = env
        };
        if (!string.IsNullOrWhiteSpace(plan.TemplateHashId))
        {
            body["template_hash_id"] = plan.TemplateHashId;
        }
        else if (!string.IsNullOrWhiteSpace(plan.Image))
        {
            // Only without a template: the template carries its own image, as RunPod's template path does.
            body["image"] = plan.Image;
            // Run the image's own entrypoint. Vast's default ('ssh', as in the vastai CLI) replaces it with an SSH server,
            // so the worker never starts. A template sets its own launch mode, so it is only sent here, as the CLI does.
            body["runtype"] = "args";
        }
        if (!string.IsNullOrWhiteSpace(plan.Label))
        {
            body["label"] = plan.Label;
        }
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
        cancel.ThrowIfCancellationRequested();
        // Never cancelled once sent: an instance Vast rents bills, and losing its ID would leave it running unreleased.
        JObject created = await ApiAsync(HttpMethod.Put, $"/api/v0/asks/{offerId}/", body, CancellationToken.None) as JObject;
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

    /// <summary>Least VRAM, in MB as Vast reports it, for '(cheapest available)': enough for a 16 GB card, which most models need.</summary>
    public const double DefaultMinGpuRamMb = 15000;

    /// <summary>
    /// Searches on-demand single-GPU offers, cheapest first, filtered to rentable/verified/non-external listings, and to
    /// <paramref name="gpuName"/> and <paramref name="minGpuRamMb"/> when given. One GPU, since the worker uses only one.
    /// </summary>
    public async Task<JArray> SearchOffersAsync(string gpuName = null, double minGpuRamMb = 0, CancellationToken cancel = default)
    {
        JObject query = new()
        {
            ["num_gpus"] = new JObject { ["eq"] = 1 },
            ["limit"] = 500,
            ["verified"] = new JObject { ["eq"] = true },
            ["external"] = new JObject { ["eq"] = false },
            ["rentable"] = new JObject { ["eq"] = true },
            ["rented"] = new JObject { ["eq"] = false },
            ["type"] = "on-demand",
            ["allocated_storage"] = Math.Max(1, plan.DiskGb),
            // Vast's own instance QA requires 64 direct ports; hosts with fewer are often behind a proxy or VPN that never forwards the mapped port.
            ["direct_port_count"] = new JObject { ["gte"] = MinDirectPorts }
            // No "order" field: Vast's current /bundles/ validation rejects the official CLI's own
            // [[field, direction]] shape (confirmed live: "order.0/order.1: Input should be a valid
            // tuple"), so cheapest-first is done client-side below instead of depending on that contract.
        };
        if (string.IsNullOrWhiteSpace(plan.TemplateHashId) && CloudBackendTypes.NeedsCuda13(plan.Image))
        {
            // Hosts whose driver cannot load the backend's kernels would rent fine and then never start it.
            query["cuda_max_good"] = new JObject { ["gte"] = 13.0 };
        }
        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            query["gpu_name"] = new JObject { ["eq"] = gpuName };
        }
        if (minGpuRamMb > 0)
        {
            query["gpu_ram"] = new JObject { ["gte"] = minGpuRamMb };
        }
        JObject resp = await ApiAsync(HttpMethod.Post, "/api/v0/bundles/", query, cancel) as JObject;
        JArray offers = resp?["offers"] as JArray ?? [];
        // Checked here too, so a filter the search ignored can never rent the wrong GPU.
        return [.. offers.Where(o => string.IsNullOrWhiteSpace(gpuName) || string.Equals(o["gpu_name"]?.ToString(), gpuName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => ((double?)o["dph_total"]) ?? double.MaxValue)];
    }

    /// <summary>
    /// Gathers everything the settings form needs to offer real choices: the GPU models on offer (with their lowest price),
    /// the account's instances, templates and network volumes. Each part degrades to empty rather than failing the whole
    /// call, matching RunPodPodsProvider.ListAccountOptionsAsync.
    /// </summary>
    public async Task<JObject> ListAccountOptionsAsync(CancellationToken cancel = default)
    {
        JArray gpus = [];
        try
        {
            gpus = GroupOffersByGpu(await SearchOffersAsync(cancel: cancel));
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VastAI Instances] Could not list offers: {ex.Message}");
        }
        JArray volumes = [];
        try
        {
            JToken resp = await ApiAsync(HttpMethod.Get, "/api/v0/volumes?owner=me&type=network_volume", null, cancel);
            foreach (JToken t in resp?["volumes"] as JArray ?? [])
            {
                if (t is not JObject v)
                {
                    continue;
                }
                volumes.Add(new JObject { ["id"] = v["id"]?.ToString(), ["name"] = v["name"]?.ToString(), ["size_gb"] = ((int?)v["size"]) });
            }
        }
        catch (CloudApiException ex) when (ex.Status is 401 or 403)
        {
            // Some Vast.ai API keys are not allowed to list volumes; that only leaves the volume dropdown empty.
            Logs.Debug($"[VastAI Instances] This API key cannot list network volumes ({ex.Status}); the volume list stays empty.");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VastAI Instances] Could not list network volumes: {ex.Message}");
        }
        JArray instances = [];
        try
        {
            foreach (JObject inst in await ListInstancesAsync(cancel))
            {
                instances.Add(new JObject
                {
                    ["id"] = inst["id"]?.ToString(),
                    ["label"] = inst["label"]?.ToString(),
                    ["status"] = (inst["actual_status"] ?? inst["cur_state"])?.ToString(),
                    ["gpu"] = inst["gpu_name"]?.ToString(),
                    ["num_gpus"] = ((int?)inst["num_gpus"])
                });
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VastAI Instances] Could not list instances: {ex.Message}");
        }
        JArray templates = [];
        try
        {
            templates = await ListOwnTemplatesAsync(cancel);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VastAI Instances] Could not list templates: {ex.Message}");
        }
        return new JObject
        {
            ["gpus"] = gpus,
            ["network_volumes"] = volumes,
            ["instances"] = instances,
            ["templates"] = templates
        };
    }

    /// <summary>One entry per GPU model in <paramref name="offers"/> (sorted cheapest first): its name, VRAM in GB, lowest price per hour and how many machines offer it.</summary>
    internal static JArray GroupOffersByGpu(JArray offers)
    {
        JArray gpus = [];
        foreach (IGrouping<string, JToken> group in offers.Where(o => !string.IsNullOrWhiteSpace(o["gpu_name"]?.ToString())).GroupBy(o => o["gpu_name"].ToString()))
        {
            gpus.Add(new JObject
            {
                ["name"] = group.Key,
                // Vast reports gpu_ram in MB (confirmed against the official CLI's own display conversion).
                ["gpu_ram"] = Math.Round((group.Max(o => (double?)o["gpu_ram"]) ?? 0) / 1000),
                ["min_price"] = group.Min(o => (double?)o["dph_total"]),
                ["offers"] = group.Count()
            });
        }
        return gpus;
    }

    /// <summary>The account's own templates (Vast's template search also returns public ones, so it is filtered to this user).</summary>
    public async Task<JArray> ListOwnTemplatesAsync(CancellationToken cancel = default)
    {
        string userId = (await ApiAsync(HttpMethod.Get, "/api/v0/users/current/", null, cancel))?["id"]?.ToString();
        if (!long.TryParse(userId, out long userNumber))
        {
            return [];
        }
        string filters = Uri.EscapeDataString(new JObject { ["creator_id"] = new JObject { ["eq"] = userNumber } }.ToString(Newtonsoft.Json.Formatting.None));
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v0/template/?select_filters={filters}", null, cancel);
        JArray templates = [];
        foreach (JObject tpl in (resp?["templates"] as JArray ?? resp as JArray ?? []).OfType<JObject>())
        {
            if (!string.Equals(tpl["creator_id"]?.ToString(), userId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(tpl["hash_id"]?.ToString()))
            {
                continue;
            }
            string image = tpl["image"]?.ToString();
            string tag = tpl["tag"]?.ToString();
            templates.Add(new JObject
            {
                ["id"] = tpl["hash_id"].ToString(),
                ["name"] = tpl["name"]?.ToString(),
                ["image"] = string.IsNullOrWhiteSpace(tag) || (image?.Contains(':') ?? false) ? image : $"{image}:{tag}"
            });
        }
        return templates;
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
    /// <summary>Waits until the address accepts a TCP connection, or refuses one (which still proves packets reach the container). False if it only timed out until <paramref name="until"/>.</summary>
    internal static async Task<bool> WaitForPortAsync(string publicUrl, DateTime until, CancellationToken cancel)
    {
        Uri uri = new(publicUrl);
        while (true)
        {
            using (CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (TcpClient client = new())
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await client.ConnectAsync(uri.Host, uri.Port, attempt.Token);
                    return true;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    return true;
                }
                catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancel.IsCancellationRequested))
                {
                    Logs.Verbose($"[VastAI Instances] {uri.Host}:{uri.Port} not reachable yet: {ex.Message}");
                }
            }
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
            await Task.Delay(5000, cancel);
        }
    }

    /// <summary>The earlier of two times.</summary>
    static DateTime Min(DateTime a, DateTime b)
    {
        return a < b ? a : b;
    }

    public async Task<JObject> GetInstanceAsync(string id, CancellationToken cancel = default)
    {
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v0/instances/{id}/?owner=me", null, cancel, allowNotFound: true);
        return resp?["instances"] as JObject;
    }

    /// <summary>Stops the instance, releasing compute but keeping its disk so it can be started again.</summary>
    public async Task StopInstanceRawAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return;
        }
        Logs.Info($"[VastAI Instances] Stopping instance '{ActiveInstanceId}'...");
        try
        {
            await ApiAsync(HttpMethod.Put, $"/api/v0/instances/{ActiveInstanceId}/", new JObject { ["state"] = "stopped" }, cancel, allowNotFound: true);
        }
        catch (CloudApiException ex) when (ex.Status is 400 or 409)
        {
            // Already stopped. Anything else propagates, so a failed stop is reported as possibly still billing.
            Logs.Verbose($"[VastAI Instances] Instance '{ActiveInstanceId}' stop returned {ex.Status}: {ex.Detail}");
        }
    }

    /// <summary>Permanently destroys the instance and its container disk. A network volume is only detached.</summary>
    public async Task DestroyInstanceAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return;
        }
        Logs.Info($"[VastAI Instances] Destroying instance '{ActiveInstanceId}'...");
        await ApiAsync(HttpMethod.Delete, $"/api/v0/instances/{ActiveInstanceId}/", null, cancel, allowNotFound: true);
    }

    /// <summary>Calls the Vast.ai console REST API via the shared client (see <see cref="CloudApiClient.ApiAsync"/>).</summary>
    public Task<JToken> ApiAsync(HttpMethod method, string path, JObject body, CancellationToken cancel = default, bool allowNotFound = false)
    {
        return Api.ApiAsync(method, path, body, cancel, allowNotFound);
    }

    /// <summary>Label prefix of every instance this extension creates, used to recognise its orphans.</summary>
    public const string ManagedLabelPrefix = "swarmui-cloudbackends";

    /// <summary>Running instances on the account that this extension created (by default label, or by an ID it remembers), as <c>{id, name, status, cost_per_hr}</c>.</summary>
    public async Task<List<JObject>> ListManagedRunningAsync(ICollection<string> knownIds, CancellationToken cancel = default)
    {
        List<JObject> result = [];
        foreach (JObject instance in (await ListInstancesAsync(cancel)).OfType<JObject>())
        {
            string label = instance["label"]?.ToString() ?? "";
            string status = instance["actual_status"]?.ToString() ?? "";
            if ((label.StartsWith(ManagedLabelPrefix, StringComparison.OrdinalIgnoreCase) || knownIds.Contains(instance["id"]?.ToString() ?? "")) && status == "running")
            {
                result.Add(new JObject { ["id"] = instance["id"]?.ToString(), ["name"] = label, ["status"] = status, ["cost_per_hr"] = instance["dph_total"] });
            }
        }
        return result;
    }

    /// <summary>Stops any instance on the account by ID (orphan cleanup). Keeps its disk.</summary>
    public async Task StopInstanceByIdAsync(string instanceId, CancellationToken cancel = default)
    {
        Logs.Info($"[VastAI Instances] Stopping instance '{instanceId}' on request...");
        await ApiAsync(HttpMethod.Put, $"/api/v0/instances/{instanceId}/", new JObject { ["state"] = "stopped" }, cancel, allowNotFound: true);
    }
}
