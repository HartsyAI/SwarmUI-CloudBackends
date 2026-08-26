using System.Collections.Concurrent;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Net.WebSockets;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Abstract T2I backend shared across all cloud GPU providers.
///
/// Subclasses implement three methods:
///   <see cref="CreateProvider"/> — return a fully-initialised <see cref="ICloudProvider"/>
///   <see cref="GetApiKey"/>     — retrieve the provider-specific API key from the user session
///   <see cref="CheckPermission"/> — throw if the user lacks permission
///
/// Everything else — worker caching, model refresh, Generate, GenerateLive, LoadModel — lives here.
/// </summary>
public abstract class CloudBackendBase : AbstractT2IBackend
{
    // ── Shared HTTP client ────────────────────────────────────────────────────

    public static System.Net.Http.HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    // ── Active provider (created in Init) ────────────────────────────────────

    public ICloudProvider Provider { get; private set; }

    // ── Runtime state ─────────────────────────────────────────────────────────

    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;
    public ConcurrentDictionary<string, string> RemoteFeatureCombo = new();
    public Session Session = null;
    public CloudWorkerInfo CurrentWorker = null;
    public DateTime WorkerKeepaliveExpiry = DateTime.MinValue;
    public SemaphoreSlim WorkerLock = new(1, 1);
    public CancellationTokenSource KeepaliveCts = null;

    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>Settings every provider shares. Subclasses extend with provider-specific fields.</summary>
    public class BaseSettings : AutoConfiguration
    {
        [ConfigComment("Cloud endpoint identifier (endpoint ID, name, pod ID — provider-specific).")]
        public string EndpointId = "";

        [ConfigComment("Max parallel generation requests.")]
        public int MaxConcurrent = 10;

        [ConfigComment("Poll interval while waiting for worker startup (ms).")]
        public int PollIntervalMs = 2000;

        [ConfigComment("Max worker startup / pod resume timeout (seconds).")]
        public int StartupTimeoutSec = 800;

        [ConfigComment("Per-generation timeout (seconds).")]
        public int GenerationTimeoutSec = 300;

        [ConfigComment("How long to keep a woken worker alive after each request (seconds).\nThis is what you pay for while idle, so lower is cheaper - but it MUST exceed your longest single generation, or the worker can be torn down mid-generation.\nAutomatically raised to at least the generation timeout plus two minutes.")]
        public int KeepaliveSeconds = 420;

        [ConfigComment("Refresh available models from the worker on backend init (background).")]
        public bool AutoRefresh = false;
    }

    /// <summary>Returns the subclass's settings cast to <see cref="BaseSettings"/>.</summary>
    public abstract BaseSettings BaseConfig { get; }

    /// <summary>
    /// Seconds to keep a woken worker alive. Deliberately NOT tied to <c>StartupTimeoutSec</c> (a cold-boot
    /// budget, often many minutes) - using that as a keepalive bills a fully idle GPU for that long after the
    /// last request. Floored at the generation timeout plus a buffer so a worker is never torn down mid-generation.
    /// </summary>
    public int KeepaliveDuration => Math.Max(Math.Max(60, BaseConfig.KeepaliveSeconds), BaseConfig.GenerationTimeoutSec + 120);

    // ── Abstract hooks for subclasses ─────────────────────────────────────────

    /// <summary>Factory: create a provider initialised with the caller's API key.</summary>
    protected abstract ICloudProvider CreateProvider(string apiKey);

    /// <summary>Retrieve the provider API key for <paramref name="session"/>. Throw a readable error if missing.</summary>
    protected abstract string GetApiKey(Session session);

    /// <summary>Throw <see cref="SwarmReadableErrorException"/> if the session user lacks permission.</summary>
    public abstract void CheckPermission(Session session);

    // ── Session-invalid exception ─────────────────────────────────────────────

    public class SessionInvalidException : Exception { }

    public static void AutoThrowException(JObject data)
    {
        if (data.TryGetValue("error_id", out JToken errorId) && errorId.ToString() == "invalid_session_id")
            throw new SessionInvalidException();
        if (data.TryGetValue("error", out JToken error))
            throw new SwarmReadableErrorException($"Remote worker gave error: {error}");
    }

    public async Task RunWithSession(Func<Task> run)
    {
        await RunWithSession(async () => { await run(); return true; });
    }

    public async Task<T> RunWithSession<T>(Func<Task<T>> run)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await run(); }
            catch (SessionInvalidException)
            {
                if (attempt >= 2)
                {
                    throw new SwarmReadableErrorException($"Remote worker session could not be recovered after {attempt + 1} attempts.");
                }
                Logs.Verbose($"[{Provider?.ProviderName}] Remote session invalid for backend {BackendData?.ID}, recovering (attempt {attempt + 1})...");
                await TryRecoverRemoteSessionAsync();
            }
        }
    }

    /// <summary>
    /// Refreshes the remote worker's session in place (the worker handler caches its session and never revalidates it,
    /// so a fresh GetNewSession against the worker is the only correct recovery). If the worker is unreachable,
    /// clears worker state so the next attempt wakes a fresh worker.
    /// </summary>
    public async Task TryRecoverRemoteSessionAsync()
    {
        CloudWorkerInfo worker = CurrentWorker;
        if (worker is not null)
        {
            try
            {
                using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
                JObject resp = await HttpClient.PostJson($"{worker.PublicUrl.TrimEnd('/')}/API/GetNewSession", [], null, cancel.Token);
                string newSession = resp["session_id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(newSession))
                {
                    worker.SessionId = newSession;
                    Logs.Verbose($"[{Provider?.ProviderName}] Remote session refreshed in place for worker {worker.WorkerId}.");
                    return;
                }
            }
            catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] Remote session refresh failed ({ex.Message}), clearing worker state to force re-wake."); }
        }
        // Only clear if this same worker is still current: another request may have already recovered
        // and published a live replacement while our GetNewSession was timing out against the dead one.
        await ClearWorkerStateAsync(worker);
    }

    // ── SwarmUI backend lifecycle ─────────────────────────────────────────────

    public override async Task Init()
    {
        AddLoadStatus($"Starting {GetType().Name} backend...");
        if (string.IsNullOrWhiteSpace(BaseConfig.EndpointId))
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus("ERROR: Endpoint ID is not configured. Set it in the backend settings.");
            return;
        }
        Session = Program.Sessions.CreateSession("internal", SessionHandler.LocalUserID);
        string apiKey;
        try { apiKey = GetApiKey(Session); }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {ex.Message}");
            return;
        }
        Provider = CreateProvider(apiKey);
        Logs.Verbose($"[{Provider.ProviderName}] Backend #{BackendData?.ID} provider created. Endpoint: {BaseConfig.EndpointId}");
        try
        {
            AddLoadStatus($"Validating {Provider.ProviderName} credentials and endpoint...");
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            await Provider.ValidateAsync(cancel.Token);
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"ERROR: {Provider.ProviderName} validation failed: {ex.Message}");
            return;
        }
        MaxUsages = Math.Max(1, BaseConfig.MaxConcurrent);
        Status = BackendStatus.RUNNING;
        CanLoadModels = true;
        AddLoadStatus($"{Provider.ProviderName} backend ready (endpoint: {BaseConfig.EndpointId}, max concurrent: {MaxUsages}).");
        if (BaseConfig.AutoRefresh)
        {
            _ = Utilities.RunCheckedTask(async () =>
            {
                try
                {
                    AddLoadStatus("Refreshing models from worker (background)...");
                    await RefreshModelsFromWorkerAsync();
                    AddLoadStatus("Model refresh complete.");
                }
                catch (Exception ex) { AddLoadStatus($"Model refresh failed: {ex.Message}"); }
            });
        }
    }

    public override async Task Shutdown()
    {
        string name = Provider?.ProviderName ?? GetType().Name;
        Logs.Info($"[{name}] Backend {BackendData?.ID} shutting down...");
        // Swap under the same lock GetOrWakeWorkerAsync uses: shutdown can land while other requests are
        // still in flight (ShutdownBackendCleanly only drains down to MaxUsages), and disposing the CTS
        // out from under them would throw ObjectDisposedException on .Token.
        CancellationTokenSource oldCts;
        await WorkerLock.WaitAsync(CancellationToken.None);
        try { oldCts = KeepaliveCts; KeepaliveCts = null; }
        finally { WorkerLock.Release(); }
        oldCts?.Cancel();
        oldCts?.Dispose();
        try { if (Provider is not null) await Provider.StopKeepaliveAsync(); }
        catch (Exception ex) { Logs.Verbose($"[{name}] StopKeepaliveAsync error: {ex.Message}"); }
        await ClearWorkerStateAsync();
        Provider?.Dispose();
        Provider = null;
        Status = BackendStatus.DISABLED;
    }

    public override IEnumerable<string> SupportedFeatures => RemoteFeatureCombo.IsEmpty ? ["text2image"] : RemoteFeatureCombo.Keys;

    // ── Direct worker HTTP calls ──────────────────────────────────────────────

    /// <summary>
    /// POST to the worker's SwarmUI API. Injects <c>session_id</c> and validates the response.
    /// All providers use direct HTTP after wakeup — no provider-specific auth envelope needed.
    /// </summary>
    public async Task<JObject> CallWorkerAPI(CloudWorkerInfo worker, string apiPath, JObject body, int timeoutSeconds = 120)
    {
        body = (JObject)body.DeepClone();
        body["session_id"] = worker.SessionId;
        string url = $"{worker.PublicUrl.TrimEnd('/')}/API/{apiPath.TrimStart('/')}";
        Logs.Verbose($"[{Provider?.ProviderName}] POST {url}");
        JObject result;
        using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            result = await HttpClient.PostJson(url, body, null, cancel.Token);
        }
        catch (OperationCanceledException) when (!Program.GlobalProgramCancel.IsCancellationRequested)
        {
            throw new SwarmReadableErrorException($"Worker API call '{apiPath}' timed out after {timeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Newtonsoft.Json.JsonException)
        {
            Logs.Verbose($"[{Provider?.ProviderName}] Worker at {worker.PublicUrl} unreachable or returned non-JSON ({ex.Message}) - treating worker as dead.");
            throw new SessionInvalidException();
        }
        AutoThrowException(result);
        return result;
    }

    // ── Worker lifecycle ──────────────────────────────────────────────────────

    public async Task<CloudWorkerInfo> GetOrWakeWorkerAsync(int keepaliveDuration)
    {
        await WorkerLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            if (CurrentWorker is not null && DateTime.UtcNow < WorkerKeepaliveExpiry)
            {
                int remaining = (int)(WorkerKeepaliveExpiry - DateTime.UtcNow).TotalSeconds;
                Logs.Debug($"[{Provider?.ProviderName}] Reusing worker {CurrentWorker.WorkerId} (expires in {remaining}s)");
                if (remaining < keepaliveDuration / 2)
                {
                    // Extend by ADDING a keepalive, never by cancelling the running one: cancelling an
                    // in-progress job makes the provider terminate the worker executing it, which would
                    // kill the very worker we are trying to keep alive.
                    Logs.Debug($"[{Provider?.ProviderName}] Extending keepalive by {keepaliveDuration}s");
                    KeepaliveCts ??= new CancellationTokenSource();
                    // Queued keepalives ADD to the worker's life, so the expiry must accumulate too.
                    // Resetting it to now+duration would make real worker life advance at twice wall-clock
                    // (extend every duration/2, buy a full duration each time) - unbounded idle billing.
                    if (await Provider.StartKeepaliveAsync(CurrentWorker, keepaliveDuration, KeepaliveCts.Token))
                    {
                        ExtendKeepaliveExpiry(keepaliveDuration);
                    }
                }
                return CurrentWorker;
            }
            Logs.Debug($"[{Provider?.ProviderName}] Waking new worker (keepalive: {keepaliveDuration}s)");
            // Cancel any stale keepalive BEFORE waking: a leftover blocking keepalive job would
            // queue-block the wakeup on the worker's single job slot (or spin up a second billed worker).
            KeepaliveCts?.Cancel();
            KeepaliveCts?.Dispose();
            KeepaliveCts = new CancellationTokenSource();
            try { await Provider.StopKeepaliveAsync(); }
            catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] StopKeepalive before wake failed: {ex.Message}"); }
            CurrentWorker = await Provider.WakeupWorkerAsync(BaseConfig.StartupTimeoutSec, BaseConfig.PollIntervalMs, Program.GlobalProgramCancel);
            // If keepalive could not be established, only trust the worker briefly - the provider will
            // reap it on its idle timeout, and claiming a full keepalive window would strand every
            // request in that window against a worker that is already gone.
            bool alive = await Provider.StartKeepaliveAsync(CurrentWorker, keepaliveDuration, KeepaliveCts.Token);
            WorkerKeepaliveExpiry = DateTime.UtcNow.AddSeconds(alive ? keepaliveDuration : 60);
            return CurrentWorker;
        }
        finally { WorkerLock.Release(); }
    }

    /// <summary>Accumulates keepalive expiry from the later of 'now' and the existing expiry.</summary>
    void ExtendKeepaliveExpiry(int keepaliveDuration)
    {
        DateTime from = WorkerKeepaliveExpiry > DateTime.UtcNow ? WorkerKeepaliveExpiry : DateTime.UtcNow;
        WorkerKeepaliveExpiry = from.AddSeconds(keepaliveDuration);
    }

    /// <summary>
    /// Tops up the keepalive during a long wait (worker boot, model indexing) so the worker is not torn
    /// down mid-operation. Only extends an existing worker - never wakes a replacement.
    /// </summary>
    public async Task RenewKeepaliveIfNeededAsync(CloudWorkerInfo worker, int keepaliveDuration)
    {
        await WorkerLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            if (!ReferenceEquals(CurrentWorker, worker) || DateTime.UtcNow < WorkerKeepaliveExpiry.AddSeconds(-90)) { return; }
            Logs.Debug($"[{Provider?.ProviderName}] Renewing keepalive during long wait (+{keepaliveDuration}s)");
            KeepaliveCts ??= new CancellationTokenSource();
            if (await Provider.StartKeepaliveAsync(worker, keepaliveDuration, KeepaliveCts.Token))
            {
                ExtendKeepaliveExpiry(keepaliveDuration);
            }
        }
        finally { WorkerLock.Release(); }
    }

    /// <summary>
    /// Clears the cached worker. Pass <paramref name="expected"/> to only clear if that exact worker is
    /// still current - otherwise a slow recovery path can wipe a worker another request just woke, which
    /// then gets torn down by the next wake's keepalive cancel while it is mid-generation.
    /// </summary>
    public async Task ClearWorkerStateAsync(CloudWorkerInfo expected = null)
    {
        await WorkerLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            if (expected is not null && !ReferenceEquals(CurrentWorker, expected)) { return; }
            CurrentWorker = null;
            WorkerKeepaliveExpiry = DateTime.MinValue;
        }
        finally { WorkerLock.Release(); }
    }

    public async Task WaitForWorkerBackendsLoadedAsync(CloudWorkerInfo worker, int timeoutSec)
    {
        int pollMs = Math.Clamp(BaseConfig.PollIntervalMs, 500, 5000);
        int attempts = Math.Max(1, (timeoutSec * 1000) / pollMs);
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                JObject data = await CallWorkerAPI(worker, "ListBackends", new JObject { ["nonreal"] = true, ["full_data"] = true });
                bool anyLoading = data.Properties().Select(p => p.Value).OfType<JObject>()
                    .Any(b => string.Equals(b["status"]?.ToString(), "loading", StringComparison.OrdinalIgnoreCase));
                UpdateFeaturesFromWorker(data);
                if (!anyLoading) return;
            }
            // A dead/invalid worker must NOT be retried here - propagate so RunWithSession can
            // refresh the remote session or wake a replacement worker.
            catch (SessionInvalidException) { throw; }
            catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] Backend poll error (attempt {i + 1}): {ex.Message}"); }
            // This wait can outlast the keepalive on a slow first boot - top it up rather than let the
            // worker be reaped out from under the request we are waiting to serve.
            await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
            await Task.Delay(pollMs);
        }
        Logs.Verbose($"[{Provider?.ProviderName}] Timed out waiting for worker backends; proceeding.");
    }

    public void UpdateFeaturesFromWorker(JObject backendData)
    {
        HashSet<string> features = ["text2image"];
        foreach (JToken backend in backendData.Values())
        {
            if (backend["status"]?.ToString() is "running" && backend["features"] is JArray arr)
                features.UnionWith(arr.Select(f => f.ToString()));
        }
        foreach (string f in features.Where(f => !RemoteFeatureCombo.ContainsKey(f))) RemoteFeatureCombo.TryAdd(f, f);
        foreach (string f in RemoteFeatureCombo.Keys.Where(f => !features.Contains(f))) RemoteFeatureCombo.TryRemove(f, out _);
    }

    // ── Model management ──────────────────────────────────────────────────────

    public override async Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        if (input?.Get(T2IParamTypes.NoLoadModels, false) is true)
        {
            CurrentModelName = model?.Name;
            return true;
        }
        try
        {
            return await RunWithSession(async () =>
            {
                CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
                await WaitForWorkerBackendsLoadedAsync(worker, BaseConfig.StartupTimeoutSec);
                string desired = model?.Name ?? GetModelFromInput(input);
                if (string.IsNullOrWhiteSpace(desired)) return false;
                foreach (string candidate in ModelCandidates(desired))
                {
                    if (await TrySelectModel(worker, candidate)) { CurrentModelName = candidate; return true; }
                }
                return false;
            });
        }
        catch (Exception ex) { Logs.Debug($"[{Provider?.ProviderName}] LoadModel failed: {ex.Message}"); return false; }
    }

    public async Task RefreshModelsFromWorkerAsync()
    {
        await RunWithSession(() => RefreshModelsInner());
    }

    async Task<bool> RefreshModelsInner()
    {
        CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
        int maxWaitSec = Math.Max(BaseConfig.StartupTimeoutSec, 120);
        DateTime start = DateTime.UtcNow;
        while (true)
        {
            try
            {
                ConcurrentDictionary<string, List<string>> tempModels = new();
                ConcurrentDictionary<string, Dictionary<string, JObject>> tempRemote = new();
                bool workerDied = false;
                await Task.WhenAll(Program.T2IModelSets.Keys.Select(subtype => Task.Run(async () =>
                {
                    try
                    {
                        JObject resp = await CallWorkerAPI(worker, "ListModels", new JObject
                        {
                            ["path"] = "", ["depth"] = 999, ["subtype"] = subtype,
                            ["allowRemote"] = false, ["dataImages"] = true
                        });
                        Dictionary<string, JObject> meta = [];
                        foreach (JToken t in resp["files"] as JArray ?? [])
                        {
                            JObject d = (t is JObject obj ? (JObject)obj.DeepClone() : new JObject { ["name"] = t.ToString() });
                            d["local"] = false;
                            d["title"] ??= d["name"]?.ToString()?.AfterLast('/') ?? "";
                            d["preview_image"] ??= "imgs/model_placeholder.jpg";
                            d["is_supported_model_format"] ??= true;
                            string name = d["name"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(name)) meta[name] = d;
                        }
                        tempModels[subtype] = [.. meta.Keys];
                        tempRemote[subtype] = meta;
                    }
                    catch (SessionInvalidException) { workerDied = true; }
                    catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] ListModels failed for '{subtype}': {ex.Message}"); }
                })));
                // Every subtype failed because the worker is gone - recover rather than re-polling a corpse.
                // Any subtype lost to a dead worker means this listing is incomplete - recovering beats
                // silently committing a partial model list as if the refresh had succeeded.
                if (workerDied) { throw new SessionInvalidException(); }
                int totalCount = tempModels.Values.Sum(l => l.Count);
                if (totalCount > 0)
                {
                    RemoteModels ??= new();
                    Models ??= new();
                    foreach (var kv in tempModels) Models[kv.Key] = kv.Value;
                    foreach (var kv in tempRemote) RemoteModels[kv.Key] = kv.Value;
                    Logs.Info($"[{Provider?.ProviderName}] Model refresh complete: {tempModels.Values.Sum(l => l.Count)} models across {tempModels.Count} subtypes.");
                    Program.ModelRefreshEvent?.Invoke();
                    return true;
                }
                if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec)
                    throw new TimeoutException($"No models discovered on the worker within {maxWaitSec}s.");
                AddLoadStatus("Waiting for Swarm to finish loading models on worker...");
                await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
                await Task.Delay(10_000);
            }
            catch (TimeoutException) { throw; }
            catch (SessionInvalidException) { throw; }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec) throw;
                Logs.Verbose($"[{Provider?.ProviderName}] Model refresh attempt failed: {ex.Message}. Retrying...");
                await Task.Delay(10_000);
            }
        }
    }

    // ── Generation ────────────────────────────────────────────────────────────

    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        if (user_input.SourceSession is not null) CheckPermission(user_input.SourceSession);
        return await RunWithSession(async () =>
        {
            // Worker acquisition must live INSIDE the retried lambda: on session recovery the
            // worker (URL + session) may have been replaced, and a stale capture would retry forever.
            CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
            await WaitForWorkerBackendsLoadedAsync(worker, BaseConfig.StartupTimeoutSec);
            JObject resp = await CallWorkerAPI(worker, "GenerateText2Image", BuildRequest(user_input, worker.SessionId), BaseConfig.GenerationTimeoutSec);
            Image[] images = ExtractImages(resp);
            if (images.Length is 0) throw new SwarmReadableErrorException("No images returned from remote worker.");
            return images;
        });
    }

    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        if (user_input.SourceSession is not null) CheckPermission(user_input.SourceSession);
        await RunWithSession(async () =>
        {
            CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
            await WaitForWorkerBackendsLoadedAsync(worker, BaseConfig.StartupTimeoutSec);
            using ClientWebSocket ws = await NetworkBackendUtils.ConnectWebsocket(worker.PublicUrl.TrimEnd('/'), "API/GenerateText2ImageWS", _ => { });
            await ws.SendJson(BuildRequest(user_input, worker.SessionId), API.WebsocketTimeout);
            bool interruptSent = false;
            while (true)
            {
                if (user_input.InterruptToken.IsCancellationRequested && !interruptSent)
                {
                    // Send once: this loop runs per received message, and each InterruptAll carries a
                    // 30s timeout, so re-sending would stall the drain until the socket finally closes.
                    interruptSent = true;
                    try { await CallWorkerAPI(worker, "InterruptAll", new JObject { ["other_sessions"] = false }, 30); }
                    catch { /* best-effort */ }
                }
                JObject response = await ws.ReceiveJson(Utilities.ExtraLargeMaxReceive, true);
                if (response is not null)
                {
                    AutoThrowException(response);
                    HandleLiveResponse(response, batchId, user_input, takeOutput);
                }
                if (ws.CloseStatus.HasValue) break;
            }
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Program.GlobalProgramCancel);
        });
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    public JObject BuildRequest(T2IParamInput input, string sessionId)
    {
        input.ProcessPromptEmbeds(x => $"<embedding:{x}>");
        JObject req = input.ToJSON();
        req["session_id"] = sessionId;
        req[T2IParamTypes.Images.Type.ID] = 1;
        req[T2IParamTypes.DoNotSave.Type.ID] = true;
        req.Remove(T2IParamTypes.ExactBackendID.Type.ID);
        req.Remove(T2IParamTypes.BackendType.Type.ID);
        if (input.ReceiveRawBackendData is not null) req[T2IParamTypes.ForwardRawBackendData.Type.ID] = true;
        req[T2IParamTypes.ForwardSwarmData.Type.ID] = true;
        return req;
    }

    public static Image[] ExtractImages(JObject response)
    {
        List<Image> images = [];
        foreach (JToken t in response["images"] as JArray ?? [])
        {
            try { images.Add(ImageFile.FromDataString(t.ToString()) as Image); }
            catch (Exception ex) { Logs.Warning($"Failed to decode image: {ex.Message}"); }
        }
        return [.. images];
    }

    public static void HandleLiveResponse(JObject response, string batchId, T2IParamInput input, Action<object> takeOutput)
    {
        if (response.TryGetValue("gen_progress", out JToken val) && val is JObject objVal)
        {
            string actualId = batchId;
            if (objVal.TryGetValue("batch_index", out JToken batchInd) && int.TryParse($"{batchInd}", out int remoteIdx)
                && remoteIdx > 0 && int.TryParse(batchId, out int localIdx))
                actualId = $"{localIdx + remoteIdx}";
            objVal["batch_index"] = actualId;
            objVal["request_id"] = $"{input.UserRequestId}";
            takeOutput(objVal);
        }
        else if (response.TryGetValue("image", out val)) { takeOutput(ImageFile.FromDataString(val.ToString())); }
        else if (response.TryGetValue("raw_backend_data", out JToken rawData))
        {
            string type = rawData["type"]?.ToString();
            string datab64 = rawData["data"]?.ToString();
            if (type is not null && datab64 is not null)
                input.ReceiveRawBackendData?.Invoke(type, Convert.FromBase64String(datab64));
        }
        else if (response.TryGetValue("raw_swarm_data", out JToken rawSwarmTok) && rawSwarmTok is JObject rawSwarm)
        {
            if (rawSwarm.TryGetValue("params_used", out JToken paramsUsed))
                foreach (JToken p in paramsUsed) input.ParamsQueried.Add($"{p}");
            if (input.Get(T2IParamTypes.ForwardSwarmData, false)) takeOutput(response);
        }
    }

    static string GetModelFromInput(T2IParamInput input)
    {
        if (input is null) return null;
        object m = input.Get(T2IParamTypes.Model);
        return m is T2IModel tm ? tm.Name : m as string;
    }

    static IEnumerable<string> ModelCandidates(string name)
    {
        string alt = name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? name[..^".safetensors".Length] : name + ".safetensors";
        string basename = name.AfterLast('/');
        string altBase = alt.AfterLast('/');
        return new[] { name, alt, basename, altBase }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    async Task<bool> TrySelectModel(CloudWorkerInfo worker, string modelName)
    {
        try
        {
            JObject resp = await CallWorkerAPI(worker, "SelectModel", new JObject { ["model"] = modelName }, 120);
            return resp.TryGetValue("success", out JToken s) && s.Value<bool>();
        }
        // A dead worker is not "this model name was wrong" - let the caller recover instead of
        // silently trying every remaining name candidate against a worker that no longer exists.
        catch (SessionInvalidException) { throw; }
        catch { return false; }
    }
}
