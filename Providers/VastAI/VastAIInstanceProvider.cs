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

    /// <summary>Port SwarmUI listens on inside the instance, declared to Vast by a Docker -p flag in the create <c>env</c>.</summary>
    public int SwarmUIPort = 7801;

    /// <summary>Instance ID remembered from a previous run, used as a reattach hint once verified live. A stale one never blocks a fresh create.</summary>
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

    /// <summary>Gateway token for the instance's SwarmUI worker, sent in its worker config when creating it.</summary>
    public string WorkerToken = "";

    /// <summary>Reach the instance over HTTPS with Vast's instance certificate, which the Hartsy Vast worker serves.</summary>
    public bool UseTls = true;

    /// <summary>If true, releasing an instance this card found or created (Stop, a failsafe, or shutdown) destroys it; if false it is only stopped, so it can resume. An instance named in <see cref="InstanceId"/> is the user's own and is only ever stopped.</summary>
    public bool DeleteOnStop = true;
}

/// <summary><see cref="ICloudInstanceProvider"/> for Vast.ai rented instances, on the console REST API (https://console.vast.ai), checked against the official `vastai` Python SDK.
/// Vast has no proxy domain: a port is reached at {public_ipaddr}:{mapped_external_port}, which is assigned randomly after creation, and a port is exposed by a Docker -p flag key in the create <c>env</c> ("-p {port}:{port}": "1") rather than a ports field.
/// Starting resolves an instance (an explicit ID, else one found by label, else a new one on the cheapest matching offer), issues a start, waits for it to run with an IP and mapped port,
/// checks that the host actually forwards the port (else destroys a just-created instance and rents another machine), then waits for SwarmUI itself.
/// Instances bill while running, so there is no keepalive: releasing stops or destroys the instance.</summary>
public class VastAIInstanceProvider(string apiKey, VastAIInstancePlan plan) : ICloudInstanceProvider
{
    /// <summary>Instance this provider is bound to. Resolved on first wake.</summary>
    public string ActiveInstanceId { get; private set; } = plan.InstanceId?.Trim() ?? "";

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    /// <remarks>Null until an instance has been resolved. Cached briefly unless <paramref name="forceRefresh"/> is set.</remarks>
    public async Task<CloudInstanceStatus> GetStatusAsync(bool forceRefresh = false, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return null;
        }
        return await StatusCache.GetAsync(forceRefresh, async () => StatusFromInstance(await GetInstanceAsync(ActiveInstanceId, cancel)), cancel);
    }

    /// <summary>Projects Vast's <c>GET /instances/{id}/</c> response into the provider-neutral status. The status word is display-only, since Vast documents no enum,
    /// so readiness is gated on connectivity instead: an IP and mapped port, then SwarmUI answering. The port map is Docker-inspect shaped, <c>ports["7801/tcp"] = [{ "HostPort": "..." }]</c>.</summary>
    CloudInstanceStatus StatusFromInstance(JObject inst)
    {
        if (inst is null)
        {
            return null;
        }
        string ip = inst["public_ipaddr"]?.ToString();
        int? mappedPort = null;
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
            PublicUrl = (!string.IsNullOrWhiteSpace(ip) && mappedPort is not null) ? $"{(plan.UseTls ? "https" : "http")}://{ip}:{mappedPort}" : null
        };
    }

    /// <inheritdoc/>
    public async Task ValidateAsync(CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveInstanceId) && string.IsNullOrWhiteSpace(plan.Image) && string.IsNullOrWhiteSpace(plan.TemplateHashId))
        {
            throw new SwarmReadableErrorException("Set 'Image' or 'TemplateHashId' so an instance can be created, or set 'InstanceId' to use an existing instance.");
        }
        await ApiAsync(HttpMethod.Get, "/api/v0/users/current/", null, cancel);
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId) && await GetInstanceAsync(ActiveInstanceId, cancel) is null)
        {
            throw new SwarmReadableErrorException($"Vast.ai instance '{ActiveInstanceId}' was not found on this account. Check the InstanceId backend setting.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>Tries up to <see cref="MaxHostAttempts"/> hosts: a created instance whose host never forwards the port is destroyed and another machine rented.</remarks>
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

    /// <summary>Starts (creating if needed) and waits for the instance. Returns null if it rented an unreachable host and destroyed it, so the caller can try another.
    /// The start is always issued, tolerating a 400 or 409 for an instance already running, since Vast publishes no list of allowed actions. A restarted instance reads "exited" until its host acts on the start, so that is fatal only after 90s.
    /// The address is assigned while the image is still pulling, so it waits for "running". Some hosts map the port but never forward it; a port that never accepts a connection means the host, not the worker.
    /// The mapped port answers before the container is listening, so SwarmUI itself is waited on last.</summary>
    async Task<CloudInstanceInfo> StartOnceAsync(int maxWaitSeconds, int pollIntervalMs, bool mayRedraw, CancellationToken cancel)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string instanceId = await ResolveInstanceAsync(cancel);
        JObject inst = await GetInstanceAsync(instanceId, cancel) ?? throw new SwarmReadableErrorException($"Vast.ai instance '{instanceId}' no longer exists.");
        Logs.Info($"[VastAI Instances] Instance '{instanceId}' status={inst["actual_status"]}");
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
        DateTime stoppedIsFatalAfter = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                JObject polled = await GetInstanceAsync(instanceId, cancel);
                liveStatus = StatusFromInstance(polled);
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
        if (plan.DeleteOnStop && string.IsNullOrWhiteSpace(plan.InstanceId))
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

    /// <inheritdoc/>
    public void Dispose() { }

    /// <summary>Finds the instance to use: an explicit ID, else a remembered one, else a previously created one by label, else creates one.
    /// A remembered ID is only a hint, verified before use. Existing instances are reused so every restart does not leave another billing instance behind.</summary>
    async Task<string> ResolveInstanceAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(ActiveInstanceId))
        {
            return ActiveInstanceId;
        }
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

    /// <summary>Creates an instance on the cheapest matching offer, picked at create time since a specific offer is one machine's slot and goes stale within minutes.
    /// Its env holds the worker config and the docker <c>-p</c> option that opens the port, since Vast's create has no ports field.
    /// Without a template it sends <c>runtype: args</c> so the image's own entrypoint runs; Vast's default, "ssh", replaces it with an SSH server and the worker never starts.
    /// The request is never cancelled once sent, since an instance Vast rents bills and losing its ID would leave it running.</summary>
    public async Task<string> CreateInstanceAsync(CancellationToken cancel = default)
    {
        string gpuName = plan.GpuName?.Trim() ?? "";
        JArray offers = await SearchOffersAsync(gpuName, gpuName.Length > 0 ? 0 : DefaultMinGpuRamMb, cancel);
        string offerId = offers.FirstOrDefault(o => !SkippedMachines.Contains(o["machine_id"]?.ToString() ?? ""))?["id"]?.ToString()
            ?? throw new SwarmReadableErrorException(gpuName.Length > 0
                ? $"No Vast.ai machine with a {gpuName} is available to rent right now. Pick another GPU, or '(cheapest available)'."
                : "No Vast.ai machine matched the default search (one GPU with at least 16 GB of VRAM). Pick a GPU from the list, or try again later.");
        CloudWorkerConfig workerConfig = new(plan.WorkerToken, plan.SwarmUIPort, plan.VolumeMountPath);
        JObject env = new()
        {
            [CloudWorkerConfig.TransportKey] = workerConfig.Encode(),
            [$"-p {plan.SwarmUIPort}:{plan.SwarmUIPort}"] = "1"
        };
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
            body["image"] = plan.Image;
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

    /// <summary>Searches on-demand single-GPU offers (the worker uses one), cheapest first, among rentable, verified, non-external listings, narrowed by <paramref name="gpuName"/> and <paramref name="minGpuRamMb"/> when given.
    /// Hosts need <see cref="MinDirectPorts"/> direct ports, as Vast's own instance QA requires, since fewer usually means a proxy or VPN that never forwards the mapped port, and a CUDA 13 driver for images that need it.
    /// Ordering and the GPU name are applied client-side: Vast's /bundles/ validation rejects the CLI's own order shape, and a filter it ignored must never rent the wrong GPU.</summary>
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
            ["direct_port_count"] = new JObject { ["gte"] = MinDirectPorts }
        };
        if (string.IsNullOrWhiteSpace(plan.TemplateHashId) && CloudBackendTypes.NeedsCuda13(plan.Image))
        {
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
        return [.. offers.Where(o => string.IsNullOrWhiteSpace(gpuName) || string.Equals(o["gpu_name"]?.ToString(), gpuName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => ((double?)o["dph_total"]) ?? double.MaxValue)];
    }

    /// <summary>Gathers what the settings form offers as choices: GPU models on offer with their lowest price, the account's instances, templates and network volumes.
    /// A part that fails comes back empty, so one unavailable list does not blank the form. Some API keys may not list volumes, which only leaves that dropdown empty.</summary>
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

    /// <summary>One entry per GPU model in <paramref name="offers"/>: its name, VRAM in GB (Vast reports MB), lowest price per hour and how many machines offer it.</summary>
    internal static JArray GroupOffersByGpu(JArray offers)
    {
        JArray gpus = [];
        foreach (IGrouping<string, JToken> group in offers.Where(o => !string.IsNullOrWhiteSpace(o["gpu_name"]?.ToString())).GroupBy(o => o["gpu_name"].ToString()))
        {
            gpus.Add(new JObject
            {
                ["name"] = group.Key,
                ["gpu_ram"] = Math.Round((group.Max(o => (double?)o["gpu_ram"]) ?? 0) / 1000),
                ["min_price"] = group.Min(o => (double?)o["dph_total"]),
                ["offers"] = group.Count()
            });
        }
        return gpus;
    }

    /// <summary>The account's own templates. Vast's template search also returns public ones, so the result is filtered to this user.</summary>
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

    /// <summary>Lists the account's instances, one page, which is plenty for finding one by label. The v1 endpoint takes its filters as JSON-encoded query parameters, not a body.</summary>
    public async Task<JArray> ListInstancesAsync(CancellationToken cancel = default)
    {
        string selectFilters = Uri.EscapeDataString(new JObject().ToString(Newtonsoft.Json.Formatting.None));
        string orderBy = Uri.EscapeDataString(new JArray(new JObject { ["col"] = "id", ["dir"] = "asc" }).ToString(Newtonsoft.Json.Formatting.None));
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v1/instances/?select_filters={selectFilters}&order_by={orderBy}&limit=50", null, cancel);
        return [.. (resp?["instances"] as JArray ?? []).OfType<JObject>()];
    }

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

    /// <summary>Gets an instance by ID, or null if it does not exist.</summary>
    public async Task<JObject> GetInstanceAsync(string id, CancellationToken cancel = default)
    {
        JToken resp = await ApiAsync(HttpMethod.Get, $"/api/v0/instances/{id}/?owner=me", null, cancel, allowNotFound: true);
        return resp?["instances"] as JObject;
    }

    /// <summary>Stops the instance, releasing compute but keeping its disk so it can be started again. A 400 or 409 means it is already stopped; anything else propagates, so a failed stop is reported as possibly still billing.</summary>
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
