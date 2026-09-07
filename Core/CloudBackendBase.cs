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

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// Abstract T2I backend shared across all cloud GPU providers.
///
/// Subclasses implement three methods:
///   <see cref="CreateProvider"/> - return a fully-initialised <see cref="ICloudProvider"/>
///   <see cref="GetApiKey"/>     - retrieve the provider-specific API key from the user session
///   <see cref="CheckPermission"/> - throw if the user lacks permission
///
/// Everything else - worker waking and keepalive, model refresh, the attached child - lives here. Generation
/// does not: core routes that to the worker's own backends, mirrored as children of the attached child, and
/// this backend only steps in to wake a worker when there are none.
/// </summary>
public abstract class CloudBackendBase : AbstractT2IBackend, ICloudBackend
{
    /// <inheritdoc/>
    public string CloudProviderName => Provider?.ProviderName;

    /// <inheritdoc/>
    public string OwnerUserId { get; set; }

    /// <summary>The owning user, resolved fresh from the user DB, or null if the user no longer exists.</summary>
    public User Owner => string.IsNullOrWhiteSpace(OwnerUserId) ? null : Program.Sessions.GetUser(OwnerUserId, makeNew: false);

    /// <summary>The API key the current <see cref="Provider"/> was built with, for rotation detection.</summary>
    protected string ProviderApiKey;

    /// <inheritdoc/>
    public bool IsUsingApiKey(string apiKey) => apiKey == ProviderApiKey;

    /// <inheritdoc/>
    /// <remarks>
    /// This backend only accepts a request when there is no woken worker to take it, and its whole job then is
    /// to wake one. Once the worker's own backends are mirrored as children, they are better candidates than
    /// this one in every way - they advertise the models and features the worker really has, rather than a
    /// cached guess - so this steps aside and lets core route to them directly.
    /// </remarks>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        string requester = input.SourceSession?.User?.UserID;
        if (requester is not null && requester != OwnerUserId)
        {
            input.RefusalReasons.Add($"{CloudProviderName ?? "Cloud"} backend #{BackendData?.ID} belongs to another user. Your own is created automatically once your API key is set in User Settings.");
            return false;
        }
        if (HasRunningGrandchild)
        {
            // Deliberately silent: this is a routing decision, not a refusal. Reasons are only shown when
            // every candidate declined, and adding one here would sit next to the mirrored backends' real
            // reason ("does not have that model") saying the worker is awake, which reads as a contradiction.
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public JObject GetStatusNet()
    {
        return new JObject
        {
            ["id"] = BackendData?.ID,
            ["title"] = Title,
            ["provider"] = CloudProviderName ?? "Unknown",
            ["kind"] = "serverless",
            ["status"] = Status.ToString(),
            ["endpoint"] = BaseConfig.EndpointId,
            ["model_count"] = Models?.Values.Sum(l => l.Count) ?? 0,
            ["worker_id"] = CurrentWorker?.WorkerId,
            ["worker_url"] = CurrentWorker?.PublicUrl,
            ["max_concurrent"] = BaseConfig.MaxConcurrent
        };
    }

    /// <summary>Shared HTTP client for direct worker API calls.</summary>
    public static System.Net.Http.HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>The active provider, created in Init (and rebuilt on API key rotation).</summary>
    public ICloudProvider Provider { get; private set; }

    // ── Runtime state ─────────────────────────────────────────────────────────

    /// <summary>Model metadata mirrored from the worker, per subtype, or null before the first refresh.</summary>
    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;

    /// <summary>Feature IDs the worker's own backends currently advertise.</summary>
    public ConcurrentDictionary<string, string> RemoteFeatureCombo = new();

    /// <summary>The currently woken worker, or null when none is trusted alive.</summary>
    public CloudWorkerInfo CurrentWorker = null;

    /// <summary>When the current worker's keepalive runs out and it can no longer be trusted.</summary>
    public DateTime WorkerKeepaliveExpiry = DateTime.MinValue;

    /// <summary>Guards all worker wake/extend/clear state transitions.</summary>
    public SemaphoreSlim WorkerLock = new(1, 1);

    /// <summary>The owner-bound swarm child attached to the live worker, or null while no worker is awake.</summary>
    public BackendHandler.BackendData ChildBackend = null;

    /// <summary>URL <see cref="ChildBackend"/> was attached to, so a worker moving is noticed.</summary>
    string ChildAddress = null;

    /// <summary>Guards attach/detach so two requests cannot race the child lifecycle.</summary>
    public SemaphoreSlim ChildLock = new(1, 1);

    /// <summary>Cancels outstanding keepalive jobs, swapped under <see cref="WorkerLock"/>.</summary>
    public CancellationTokenSource KeepaliveCts = null;

    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>Settings every provider shares. Subclasses extend with provider-specific fields.</summary>
    public class BaseSettings : AutoConfiguration
    {
        [ConfigComment("Cloud endpoint identifier (endpoint ID, name, pod ID - provider-specific).")]
        public string EndpointId = "";

        [ConfigComment("Unused. Parallelism is set by the worker's own backends, which report their real limits once a worker is awake.\nKept so existing configs still load.")]
        public int MaxConcurrent = 10;

        [ConfigComment("Poll interval while waiting for worker startup (ms).")]
        public int PollIntervalMs = 2000;

        [ConfigComment("Max worker startup / pod resume timeout (seconds).")]
        public int StartupTimeoutSec = 800;

        [ConfigComment("Per-generation timeout (seconds).")]
        public int GenerationTimeoutSec = 300;

        [ConfigComment("How long to keep a woken worker alive after each request (seconds).\nThis is what you pay for while idle, so lower is cheaper - but it MUST exceed your longest single generation, or the worker can be torn down mid-generation.\nAutomatically raised to at least the generation timeout plus two minutes.")]
        public int KeepaliveSeconds = 420;
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

    /// <summary>Retrieve the provider API key for <paramref name="user"/>. Throw a readable error if missing.</summary>
    protected abstract string GetApiKey(User user);

    /// <summary>Throw <see cref="SwarmReadableErrorException"/> if the session user lacks permission.</summary>
    public abstract void CheckPermission(Session session);

    /// <summary>
    /// Throws if this backend's settings are not usable. Runs before the provider is built, so it can
    /// only see settings. Providers that identify their target by something other than an endpoint ID
    /// (a pod ID, for instance) override this.
    /// </summary>
    protected virtual void CheckRequiredConfig()
    {
        if (string.IsNullOrWhiteSpace(BaseConfig.EndpointId))
        {
            throw new SwarmReadableErrorException("Endpoint ID is not configured. Set it in the backend settings.");
        }
    }

    // ── Session-invalid exception ─────────────────────────────────────────────

    /// <summary>Thrown when the remote worker is unreachable or refused our session, signaling the recovery path.</summary>
    public class SessionInvalidException : Exception { }

    /// <summary>Throws the matching exception if a worker API response carries an error.</summary>
    public static void AutoThrowException(JObject data)
    {
        if (data.TryGetValue("error_id", out JToken errorId) && errorId.ToString() == "invalid_session_id")
        {
            throw new SessionInvalidException();
        }
        if (data.TryGetValue("error", out JToken error))
        {
            throw new SwarmReadableErrorException($"Remote worker gave error: {error}");
        }
    }

    /// <summary>Runs an action against the worker, recovering the remote session and retrying on <see cref="SessionInvalidException"/>.</summary>
    public async Task RunWithSession(Func<Task> run)
    {
        await RunWithSession(async () => { await run(); return true; });
    }

    /// <summary>Same as <see cref="RunWithSession(Func{Task})"/>, returning the action's result.</summary>
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
        // The child points at the worker we just gave up on, so it goes with it. Leaving it would have the
        // retry route straight back to a dead URL instead of waking a replacement.
        await DetachChildAsync();
    }

    // ── SwarmUI backend lifecycle ─────────────────────────────────────────────

    public override async Task Init()
    {
        AddLoadStatus($"Starting {GetType().Name} backend...");
        try { CheckRequiredConfig(); }
        catch (Exception ex)
        {
            AddLoadStatus($"ERROR: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        string apiKey;
        try
        {
            // Every cloud backend runs on its owner's own key - there is deliberately no fallback to
            // any other user's key, so a misconfigured owner is a hard error rather than a mis-bill.
            User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend has no valid owner user ('{OwnerUserId}').");
            apiKey = GetApiKey(owner);
        }
        catch (Exception ex)
        {
            AddLoadStatus($"ERROR: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        ProviderApiKey = apiKey;
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
            AddLoadStatus($"ERROR: {Provider.ProviderName} validation failed: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        // One at a time, deliberately. This backend now only serves the request that wakes a worker; the
        // moment that request has one, the worker's own backends are mirrored as children and core routes to
        // them instead. A second request arriving during a wake therefore waits rather than being handed a
        // duplicate cold start, and picks up the mirrored backends once they exist.
        MaxUsages = 1;
        Status = BackendStatus.RUNNING;
        CanLoadModels = true;
        // Defensive: a re-enable can Init without a matching Shutdown, and a double subscription would run the
        // sleep check twice per tick.
        Program.TickEvent -= SleepTick;
        Program.TickEvent += SleepTick;
        // Models are deliberately NOT refreshed here: listing them wakes a billed worker, and a backend
        // that comes into being because its owner generated something must never spend money on its own.
        // The CloudRefreshModels route is the explicit, user-initiated way to do it.
        AddLoadStatus($"{Provider.ProviderName} backend ready (endpoint: {BaseConfig.EndpointId}, max concurrent: {MaxUsages}).");
    }

    public override async Task Shutdown()
    {
        Program.TickEvent -= SleepTick;
        string name = Provider?.ProviderName ?? GetType().Name;
        Logs.Info($"[{name}] Backend {BackendData?.ID} shutting down...");
        await DetachChildAsync();
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
    /// All providers use direct HTTP after wakeup - no provider-specific auth envelope needed.
    /// </summary>
    public async Task<JObject> CallWorkerAPI(CloudWorkerInfo worker, string apiPath, JObject body, int timeoutSeconds = 120)
    {
        body = (JObject)body.DeepClone();
        body["session_id"] = worker.SessionId;
        string url = $"{worker.PublicUrl.TrimEnd('/')}/API/{apiPath.TrimStart('/')}";
        Logs.Verbose($"[{Provider?.ProviderName}] POST {url}");
        JObject result;
        using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        // A freshly woken worker's proxy serves empty bodies for a few seconds before routing is live,
        // which is indistinguishable from a dead worker. Retry briefly so a cold start is not mistaken
        // for death (which would abandon the worker we just paid to wake and start another).
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                result = await HttpClient.PostJson(url, body, null, cancel.Token);
                break;
            }
            catch (OperationCanceledException) when (!Program.GlobalProgramCancel.IsCancellationRequested)
            {
                throw new SwarmReadableErrorException($"Worker API call '{apiPath}' timed out after {timeoutSeconds}s.");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Newtonsoft.Json.JsonException)
            {
                if (attempt >= 2)
                {
                    Logs.Verbose($"[{Provider?.ProviderName}] Worker at {worker.PublicUrl} unreachable or returned non-JSON ({ex.Message}) - treating worker as dead.");
                    throw new SessionInvalidException();
                }
                Logs.Verbose($"[{Provider?.ProviderName}] Worker at {worker.PublicUrl} not answering yet ({ex.Message}); retry {attempt + 1}/2...");
                try { await Task.Delay(2000, cancel.Token); }
                catch (OperationCanceledException) { throw new SwarmReadableErrorException($"Worker API call '{apiPath}' timed out after {timeoutSeconds}s."); }
            }
        }
        AutoThrowException(result);
        return result;
    }

    // ── Worker lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Re-resolves the owner's API key and rebuilds the provider if it changed, so a key rotation takes
    /// effect on the next acquisition instead of requiring a disable/re-enable cycle. Must be called
    /// under <see cref="WorkerLock"/>. Clears cached worker state on change - the old worker was woken
    /// under the old key and its keepalive would no longer be authorized.
    /// </summary>
    void RefreshProviderIfKeyChanged()
    {
        User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend's owner user ('{OwnerUserId}') no longer exists.");
        string currentKey = GetApiKey(owner);
        if (currentKey == ProviderApiKey)
        {
            return;
        }
        Logs.Info($"[{Provider?.ProviderName}] API key changed for user '{OwnerUserId}', rebuilding provider for backend #{BackendData?.ID}.");
        ICloudProvider oldProvider = Provider;
        Provider = CreateProvider(currentKey);
        ProviderApiKey = currentKey;
        oldProvider?.Dispose();
        CurrentWorker = null;
        WorkerKeepaliveExpiry = DateTime.MinValue;
    }

    /// <summary>Returns the cached live worker, extending its keepalive when close to expiry, or wakes a fresh one.</summary>
    public async Task<CloudWorkerInfo> GetOrWakeWorkerAsync(int keepaliveDuration)
    {
        await WorkerLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            RefreshProviderIfKeyChanged();
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

    // ── Child backend lifecycle ───────────────────────────────────────────────
    // A serverless worker only exists in bursts, so unlike an instance the child is attached per wake and
    // removed once the worker is gone. Nothing is attached while asleep, which is what guarantees no polling
    // can wake a billed worker behind the owner's back: SwarmSwarmBackend's idle monitor only ever re-polls a
    // worker that is already awake and already being paid for.

    /// <summary>Attaches a child to the given worker, replacing one pointed at a stale URL.</summary>
    public async Task EnsureChildAttachedAsync(CloudWorkerInfo worker)
    {
        string address = worker.PublicUrl.TrimEnd('/');
        await ChildLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            if (ChildBackend is not null && ChildAddress == address)
            {
                return;
            }
            // A wake can land on a different worker than last time, and the URL carries the host and port, so
            // a surviving child would be pointed at something that no longer exists. Replace rather than
            // re-address: SwarmSwarmBackend builds its whole mirrored subtree against the address it loaded with.
            if (ChildBackend is not null)
            {
                await DetachChildInnerAsync();
            }
            ChildBackend = CloudChildBackend.Attach(this, address, $"[{Provider?.ProviderName} worker {worker.WorkerId}] Cloud Serverless", BaseConfig.StartupTimeoutSec);
            ChildAddress = address;
            Logs.Info($"[{Provider?.ProviderName}] Attached Swarm backend #{ChildBackend.ID} to worker '{worker.WorkerId}'.");
        }
        finally { ChildLock.Release(); }
    }

    /// <summary>Removes the attached child, if any.</summary>
    public async Task DetachChildAsync()
    {
        await ChildLock.WaitAsync(CancellationToken.None);
        try { await DetachChildInnerAsync(); }
        finally { ChildLock.Release(); }
    }

    /// <summary>Detach body. Caller must hold <see cref="ChildLock"/>.</summary>
    async Task DetachChildInnerAsync()
    {
        // Last chance to keep what the worker knew: once the child is gone so is its mirror of the worker's
        // models, and this backend has to be able to answer for them while asleep.
        AdoptChildModelLists();
        BackendHandler.BackendData child = ChildBackend;
        ChildBackend = null;
        ChildAddress = null;
        await CloudChildBackend.DetachAsync(Handler, child, Provider?.ProviderName);
    }

    /// <summary>Throttles <see cref="SleepTick"/>, which core fires roughly once a second.</summary>
    long LastSleepCheck = 0;

    /// <summary>
    /// Drops the child once its worker's keepalive has run out, so nothing is left polling a URL that no
    /// longer answers and the backend list does not accumulate dead subtrees. Cost is already handled by the
    /// keepalive expiring on the provider's side; this is the local half of going back to sleep.
    /// </summary>
    void SleepTick()
    {
        if (Environment.TickCount64 < LastSleepCheck + 5000)
        {
            return;
        }
        LastSleepCheck = Environment.TickCount64;
        if (ChildBackend is null || (CurrentWorker is not null && DateTime.UtcNow < WorkerKeepaliveExpiry))
        {
            return;
        }
        // Expiry is the worker's clock, not the request's: a generation already accepted keeps running on the
        // remote, and detaching mid-flight would drop its results. The keepalive top-up on generation start
        // means this only lingers for work that really is still going.
        if (BackendData.CheckIsInUseAtAll || Grandchildren.Any(d => d.CheckIsInUseAtAll))
        {
            return;
        }
        Utilities.RunCheckedTask(() => DetachChildAsync(), $"{Provider?.ProviderName} detach idle child backend");
    }

    /// <summary>
    /// Copies the attached child's mirrored model lists onto this backend. The child gets them from the
    /// worker's own Swarm, which is the real list rather than anything guessed here, and keeping a copy after
    /// the worker sleeps is what lets a sleeping endpoint still offer its models: the UI reads them through
    /// <c>ExtraModelProviders</c>, and core's filter needs them to know a request is worth waking for.
    /// Copied rather than aliased so a detached child cannot mutate what is now this backend's cache.
    /// </summary>
    void AdoptChildModelLists()
    {
        if (ChildBackend?.AbstractBackend is not SwarmSwarmBackend swarm || swarm.RemoteModels is null)
        {
            return;
        }
        // Only overwrite with a real listing; an empty one means the child has not finished mirroring yet, and
        // committing that would drop a good cache and make every model look unavailable while asleep.
        if (!swarm.RemoteModels.Any(kv => kv.Value.Count > 0))
        {
            return;
        }
        RemoteModels ??= new();
        Models ??= new();
        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in swarm.RemoteModels)
        {
            RemoteModels[kv.Key] = kv.Value;
            Models[kv.Key] = [.. kv.Value.Keys];
        }
        Program.ModelRefreshEvent?.Invoke();
    }

    /// <summary>The generating backends the attached child mirrors from the worker. Empty while asleep.</summary>
    public IEnumerable<BackendHandler.T2IBackendData> Grandchildren
        => ChildBackend?.AbstractBackend is SwarmSwarmBackend swarm ? swarm.ControlledNonrealBackends.Values : [];

    /// <summary>True once at least one mirrored backend of the <em>current</em> worker is up and able to generate.</summary>
    /// <remarks>The address check is not redundant. A child briefly outlives its worker (a wake can replace the
    /// worker before the sleep check has dropped the old child), and its mirrored backends keep reporting
    /// RUNNING until their idle monitor notices. Without this, that stale subtree would make this backend step
    /// aside, and every request would be routed to a worker that no longer exists with nothing left to
    /// re-attach a good child.</remarks>
    public bool HasRunningGrandchild
        => ChildAddress is not null && ChildAddress == CurrentWorker?.PublicUrl?.TrimEnd('/')
            && Grandchildren.Any(d => d.Backend.Status == BackendStatus.RUNNING);

    /// <inheritdoc/>
    /// <remarks>Tops up an already-woken worker only. It must never wake one: core routes generations to the
    /// child, so this runs on any generation the child accepts, and waking here would spend the owner's money
    /// on a path they did not explicitly ask to.</remarks>
    public async Task OnChildGenerationStartingAsync()
    {
        CloudWorkerInfo worker = CurrentWorker;
        if (worker is not null)
        {
            await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
        }
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

    /// <summary>
    /// Waits until the worker's Swarm has finished loading its own backends. A worker that never becomes
    /// reachable within the timeout throws <see cref="SessionInvalidException"/> so the caller recovers,
    /// rather than proceeding to issue doomed calls against it.
    /// </summary>
    public async Task WaitForWorkerBackendsLoadedAsync(CloudWorkerInfo worker, int timeoutSec)
    {
        int pollMs = Math.Clamp(BaseConfig.PollIntervalMs, 500, 5000);
        int attempts = Math.Max(1, (timeoutSec * 1000) / pollMs);
        bool everReachable = false, everSawBackend = false;
        DateTime start = DateTime.UtcNow;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                JObject data = await CallWorkerAPI(worker, "ListBackends", new JObject { ["nonreal"] = true, ["full_data"] = true });
                everReachable = true;
                JObject[] remoteBackends = [.. data.Properties().Select(p => p.Value).OfType<JObject>()];
                bool anyLoading = remoteBackends.Any(b => string.Equals(b["status"]?.ToString(), "loading", StringComparison.OrdinalIgnoreCase));
                // A just-booted Swarm reports NO backends at all, and "none are loading" would read as
                // "all ready" - so require at least one actually running backend before proceeding.
                bool anyRunning = remoteBackends.Any(b => string.Equals(b["status"]?.ToString(), "running", StringComparison.OrdinalIgnoreCase));
                everSawBackend |= remoteBackends.Length > 0;
                UpdateFeaturesFromWorker(data);
                if (anyRunning && !anyLoading) { return; }
                // Swarm is up and answering but lists no backends at all: that is a worker configuration
                // problem, not boot lag, so say so promptly instead of burning the full startup timeout.
                if (!everSawBackend && (DateTime.UtcNow - start).TotalSeconds > 90)
                {
                    throw new SwarmReadableErrorException($"Cloud worker '{worker.WorkerId}' is running SwarmUI, but that SwarmUI has no backends configured, so it cannot generate anything. Open the worker's SwarmUI at {worker.PublicUrl} under Server -> Backends and add a backend (or rebuild the worker image with one).");
                }
                Logs.Verbose($"[{Provider?.ProviderName}] Worker Swarm has {remoteBackends.Length} backend(s), running={anyRunning}, loading={anyLoading} (poll {i + 1}/{attempts})...");
            }
            // This IS the readiness wait: a not-yet-reachable worker is the normal cold-start case, so keep
            // polling instead of declaring it dead on the first empty response. If it never answers within
            // the whole timeout, propagate so RunWithSession recovers rather than proceeding blindly.
            catch (SessionInvalidException) { Logs.Verbose($"[{Provider?.ProviderName}] Worker not reachable yet (poll {i + 1}/{attempts})..."); }
            catch (SwarmReadableErrorException) { throw; }
            catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] Backend poll error (attempt {i + 1}): {ex.Message}"); }
            // This wait can outlast the keepalive on a slow first boot - top it up rather than let the
            // worker be reaped out from under the request we are waiting to serve.
            await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
            await Task.Delay(pollMs);
        }
        if (!everReachable)
        {
            Logs.Warning($"[{Provider?.ProviderName}] Worker {worker.WorkerId} never became reachable within {timeoutSec}s.");
            throw new SessionInvalidException();
        }
        throw new SwarmReadableErrorException($"Cloud worker '{worker.WorkerId}' did not bring up a usable backend within {timeoutSec}s. Check the worker's SwarmUI at {worker.PublicUrl} under Server -> Backends.");
    }

    /// <summary>Syncs <see cref="RemoteFeatureCombo"/> from a worker's ListBackends response.</summary>
    public void UpdateFeaturesFromWorker(JObject backendData)
    {
        HashSet<string> features = ["text2image"];
        foreach (JToken backend in backendData.Values())
        {
            if (backend["status"]?.ToString() is "running" && backend["features"] is JArray arr)
            {
                features.UnionWith(arr.Select(f => f.ToString()));
            }
        }
        foreach (string f in features.Where(f => !RemoteFeatureCombo.ContainsKey(f))) { RemoteFeatureCombo.TryAdd(f, f); }
        foreach (string f in RemoteFeatureCombo.Keys.Where(f => !features.Contains(f))) { RemoteFeatureCombo.TryRemove(f, out _); }
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
                if (string.IsNullOrWhiteSpace(desired)) { return false; }
                foreach (string candidate in ModelCandidates(desired))
                {
                    if (await TrySelectModel(worker, candidate)) { CurrentModelName = candidate; return true; }
                }
                throw new SwarmReadableErrorException($"Cloud worker '{worker.WorkerId}' refused to load model '{desired}'. Check that this model exists on the worker and that the worker's backend supports it.");
            });
        }
        // Readable reasons MUST propagate: BackendHandler only records a fail reason for the user when
        // LoadModel throws (BackendHandler.cs:1405). Returning false discards it and the user is left
        // with a bare "All available backends failed to load the model ''".
        catch (SwarmReadableErrorException) { throw; }
        catch (Exception ex) { Logs.Debug($"[{Provider?.ProviderName}] LoadModel failed: {ex.Message}"); return false; }
    }

    /// <summary>Wakes the worker (if needed) and re-mirrors its model lists into <see cref="RemoteModels"/>.</summary>
    public async Task RefreshModelsFromWorkerAsync()
    {
        await RunWithSession(() => RefreshModelsInner());
    }

    async Task<bool> RefreshModelsInner()
    {
        CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
        // Wait for the worker to actually answer before fanning out ListModels, otherwise a cold start
        // makes every subtype fail at once and looks like a dead worker.
        await WaitForWorkerBackendsLoadedAsync(worker, BaseConfig.StartupTimeoutSec);
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
                            if (!string.IsNullOrWhiteSpace(name)) { meta[name] = d; }
                        }
                        tempModels[subtype] = [.. meta.Keys];
                        tempRemote[subtype] = meta;
                    }
                    catch (SessionInvalidException) { workerDied = true; }
                    catch (Exception ex) { Logs.Verbose($"[{Provider?.ProviderName}] ListModels failed for '{subtype}': {ex.Message}"); }
                })));
                // Every subtype failed because the worker is gone - recover rather than re-polling a corpse.
                // Nothing came back at all: the worker is gone, so recover instead of re-polling a corpse.
                if (workerDied && tempModels.IsEmpty()) { throw new SessionInvalidException(); }
                int totalCount = tempModels.Values.Sum(l => l.Count);
                // A partial listing must not be committed as success - retry the loop instead, which is
                // bounded by maxWaitSec below.
                if (totalCount > 0 && !workerDied)
                {
                    RemoteModels ??= new();
                    Models ??= new();
                    foreach (KeyValuePair<string, List<string>> kv in tempModels) { Models[kv.Key] = kv.Value; }
                    foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in tempRemote) { RemoteModels[kv.Key] = kv.Value; }
                    Logs.Info($"[{Provider?.ProviderName}] Model refresh complete: {tempModels.Values.Sum(l => l.Count)} models across {tempModels.Count} subtypes.");
                    Program.ModelRefreshEvent?.Invoke();
                    return true;
                }
                if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec)
                {
                    throw new TimeoutException($"No models discovered on the worker within {maxWaitSec}s.");
                }
                AddLoadStatus("Waiting for Swarm to finish loading models on worker...");
                await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
                await Task.Delay(10_000);
            }
            catch (TimeoutException) { throw; }
            catch (SessionInvalidException) { throw; }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - start).TotalSeconds >= maxWaitSec) { throw; }
                Logs.Verbose($"[{Provider?.ProviderName}] Model refresh attempt failed: {ex.Message}. Retrying...");
                await Task.Delay(10_000);
            }
        }
    }

    // ── Generation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Wakes a worker, attaches the child, and returns one of the worker's own mirrored backends to generate
    /// on. Only the cold path reaches this: once a mirrored backend is running, core routes straight to it and
    /// this backend refuses requests.
    /// </summary>
    async Task<BackendHandler.T2IBackendData> WakeAndGetGeneratorAsync(T2IParamInput user_input)
    {
        CloudWorkerInfo worker = await GetOrWakeWorkerAsync(KeepaliveDuration);
        await WaitForWorkerBackendsLoadedAsync(worker, BaseConfig.StartupTimeoutSec);
        await EnsureChildAttachedAsync(worker);
        // The child mirrors the worker in the background, so its own children appear a moment after attach.
        int pollMs = Math.Clamp(BaseConfig.PollIntervalMs, 250, 2000);
        int attempts = Math.Max(1, (BaseConfig.StartupTimeoutSec * 1000) / pollMs);
        for (int i = 0; i < attempts; i++)
        {
            List<BackendHandler.T2IBackendData> running = [.. Grandchildren.Where(d => d.Backend.Status == BackendStatus.RUNNING)];
            // Wait for the whole mirror rather than pouncing on the first backend up: on a worker running more
            // than one, an early one that cannot serve this request would otherwise fail it while the one that
            // could was still loading. Give up waiting halfway through, though - a backend stuck loading on the
            // worker forever must not block a request that another backend there could already have served.
            bool settled = (ChildBackend?.AbstractBackend as SwarmSwarmBackend)?.AnyLoading is false || i > attempts / 2;
            if (running.Count is not 0 && settled)
            {
                AdoptChildModelLists();
                // Prefer a free one, but a busy one that can serve is still better than failing: it queues.
                BackendHandler.T2IBackendData ready = running.FirstOrDefault(d => !d.CheckIsInUse && CanServe(d, user_input))
                    ?? running.FirstOrDefault(d => CanServe(d, user_input));
                if (ready is not null)
                {
                    return ready;
                }
                throw new SwarmReadableErrorException($"{CloudProviderName ?? "Cloud"} worker is up, but none of its backends accept this request.");
            }
            await RenewKeepaliveIfNeededAsync(worker, KeepaliveDuration);
            await Task.Delay(pollMs, Program.GlobalProgramCancel);
        }
        throw new SwarmReadableErrorException($"{CloudProviderName ?? "Cloud"} worker woke, but none of its backends became usable in time.");
    }

    /// <summary>
    /// Whether a mirrored backend would accept this request, asked the same way core would have asked had it
    /// routed there itself. Handing a request to a backend that has not been asked is not safe: the child pins
    /// the remote backend by ID, so a wrong pick fails outright rather than being re-routed on the worker.
    /// </summary>
    static bool CanServe(BackendHandler.T2IBackendData data, T2IParamInput input)
    {
        HashSet<string> features = [.. data.Backend.SupportedFeatures];
        if (input.RequiredFlags.Any(f => !features.Contains(f) && !T2IEngine.DisregardedFeatureFlags.Contains(f)))
        {
            return false;
        }
        return data.Backend.IsValidForThisBackend(input);
    }

    /// <remarks>
    /// Generation itself belongs to the worker's own mirrored backends, which speak Swarm's remote protocol
    /// properly - sessions, previews, interrupts and all. This path exists only because a sleeping endpoint has
    /// no mirrored backend for core to pick yet, so the request that wakes the worker is handed on by hand.
    /// </remarks>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        if (user_input.SourceSession is not null) { CheckPermission(user_input.SourceSession); }
        // Wrapped so a worker that dies between waking and being ready is recovered rather than cached as live:
        // without this the dead worker is reused, and every request until its keepalive lapses fails the same way.
        // Claim the mirrored backend for real, too. Core reserved this backend, not that one, so without a claim
        // a request arriving mid-generation would see it idle and double-book a remote that allows one job.
        using T2IBackendAccess access = new(await RunWithSession(() => WakeAndGetGeneratorAsync(user_input)));
        return await access.Backend.Generate(user_input);
    }

    /// <inheritdoc cref="Generate"/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        if (user_input.SourceSession is not null) { CheckPermission(user_input.SourceSession); }
        using T2IBackendAccess access = new(await RunWithSession(() => WakeAndGetGeneratorAsync(user_input)));
        await access.Backend.GenerateLive(user_input, batchId, takeOutput);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    static string GetModelFromInput(T2IParamInput input)
    {
        if (input is null) { return null; }
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
