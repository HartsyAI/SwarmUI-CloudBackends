using System.Collections.Concurrent;
using System.Net.Http;
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
/// A serverless cloud GPU backend (RunPod Serverless, Vast.ai Serverless), owned by one user.
///
/// It never generates itself. Each worker is held by a provider-native <b>lease</b> (see <see cref="ICloudProvider"/>)
/// and gets an owner-bound swarm child attached, which mirrors the worker's own backends; core routes generations
/// straight to those. This backend only takes a request when no free mirrored backend can, and its job then is to
/// lease another worker, up to <see cref="BaseSettings.MaxWorkers"/>, and hand the request over. Workers are let go
/// when idle, by the worker itself (RunPod) or by the lease lapsing (Vast.ai), so an idle endpoint costs nothing.
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

    /// <summary>The active provider, created in Init (and rebuilt on API key rotation).</summary>
    public ICloudProvider Provider { get; private set; }

    /// <summary>Shared client for this extension's own calls to worker gateways (TLS to Vast goes through the relay).</summary>
    public static readonly HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    // ── Runtime state ─────────────────────────────────────────────────────────

    /// <summary>Model metadata known for this endpoint, per subtype, or null before it has ever been learned.</summary>
    public ConcurrentDictionary<string, Dictionary<string, JObject>> RemoteModels = null;

    /// <summary>Feature IDs the workers' own backends advertise.</summary>
    public ConcurrentDictionary<string, string> RemoteFeatureCombo = new();

    /// <summary>Leased workers. Read freely from snapshots; modify only under <see cref="SlotLock"/>.</summary>
    public volatile WorkerSlot[] Slots = [];

    /// <summary>Guards changes to <see cref="Slots"/> and the pick-and-claim of a mirrored backend.</summary>
    public readonly SemaphoreSlim SlotLock = new(1, 1);

    /// <summary>Leases being acquired right now.</summary>
    int PendingAcquires = 0;

    /// <summary>True while a maintenance tick is running, so ticks never overlap.</summary>
    int TickRunning = 0;

    /// <summary>Throttles <see cref="OnTick"/>, which core fires about once a second.</summary>
    long NextTick = 0;

    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>Settings every serverless provider shares.</summary>
    public class BaseSettings : AutoConfiguration
    {
        [ConfigComment("Cloud endpoint identifier (RunPod endpoint ID, or Vast.ai endpoint name).")]
        public string EndpointId = "";

        [ConfigComment("Most workers this backend may run at once. When every running worker is busy, another one is started, up to this many.")]
        public int MaxWorkers = 1;

        [ConfigComment("How long a worker may sit with no generation before it shuts down, in seconds.")]
        public int IdleSeconds = 120;

        [ConfigComment("Longest a single worker lease may last, in seconds (RunPod only; the endpoint's execution timeout must be longer).")]
        public int MaxLeaseSeconds = 3600;

        [ConfigComment("How long to wait for a worker to start and answer, in seconds.")]
        public int StartupTimeoutSec = 800;

        [ConfigComment("How often to poll while waiting for a worker, in milliseconds.")]
        public int PollIntervalMs = 2000;
    }

    /// <summary>Returns the subclass's settings cast to <see cref="BaseSettings"/>.</summary>
    public abstract BaseSettings BaseConfig { get; }

    /// <summary><see cref="BaseSettings.MaxWorkers"/>, within sane bounds.</summary>
    public int MaxWorkers => Math.Clamp(BaseConfig.MaxWorkers, 1, 64);

    /// <summary>What a new lease asks for, from the current settings.</summary>
    public LeaseRequest MakeLeaseRequest()
    {
        return new LeaseRequest
        {
            IdleSeconds = Math.Clamp(BaseConfig.IdleSeconds, 5, 3600),
            // The worker must wait long enough for this side to attach and for its own backend to load.
            StartupGraceSeconds = Math.Clamp(BaseConfig.StartupTimeoutSec, 60, 3600),
            MaxLeaseSeconds = Math.Max(60, BaseConfig.MaxLeaseSeconds),
            StartupTimeoutSec = Math.Max(30, BaseConfig.StartupTimeoutSec),
            PollIntervalMs = Math.Clamp(BaseConfig.PollIntervalMs, 250, 15000)
        };
    }

    // ── Abstract hooks for subclasses ─────────────────────────────────────────

    /// <summary>Factory: create a provider initialised with the owner's API key.</summary>
    protected abstract ICloudProvider CreateProvider(string apiKey);

    /// <summary>Retrieve the provider API key for <paramref name="user"/>. Throw a readable error if missing.</summary>
    protected abstract string GetApiKey(User user);

    /// <summary>Throw <see cref="SwarmReadableErrorException"/> if the session user lacks permission.</summary>
    public abstract void CheckPermission(Session session);

    /// <summary>Throws if this backend's settings are not usable. Runs before the provider is built.</summary>
    protected virtual void CheckRequiredConfig()
    {
        if (string.IsNullOrWhiteSpace(BaseConfig.EndpointId))
        {
            throw new SwarmReadableErrorException("Endpoint ID is not configured. Set it in the backend settings.");
        }
    }

    // ── Routing ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Accepts a request only when this backend would be the right one to take it: it belongs to the requester, no
    /// free local (or already-rented) backend could serve it, no free mirrored backend of a leased worker could
    /// serve it, and there is room to lease another worker. Everything else is declined silently, because core then
    /// routes to the better candidate on its own. Declining is also what keeps cost down: accepting starts a lease.
    /// </remarks>
    public override bool IsValidForThisBackend(T2IParamInput input)
    {
        string requester = input.SourceSession?.User?.UserID;
        if (requester is not null && requester != OwnerUserId)
        {
            input.RefusalReasons.Add($"{CloudProviderName ?? "Cloud"} backend #{BackendData?.ID} belongs to another user. Your own is created automatically once your API key is set in User Settings.");
            return false;
        }
        if (IsAnyBackendType(input) && LocalBackendCanServe(input))
        {
            return false;
        }
        WorkerSlot[] slots = Slots;
        if (slots.Any(s => s.RunningGrandchildren.Any(d => !d.CheckIsInUse && CanServe(d, input))))
        {
            return false;
        }
        // At capacity with workers up: the request waits on their backends instead of starting another lease.
        // Before any worker is up, keep accepting so the request queues here rather than failing for lack of a
        // candidate; the acquisition already underway will serve it.
        bool anyRunning = slots.Any(s => s.RunningGrandchildren.Any());
        if (anyRunning && slots.Length + PendingAcquires >= MaxWorkers)
        {
            return false;
        }
        return true;
    }

    /// <summary>True unless the request explicitly targets a backend type.</summary>
    static bool IsAnyBackendType(T2IParamInput input)
    {
        string type = input.Get(T2IParamTypes.BackendType, "Any") ?? "Any";
        return type.Equals("Any", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if a free backend outside any serverless subtree (a local GPU, or an instance the user already rents) can
    /// serve this request. Core may pick any valid backend to load a model on, including this one, so without this a
    /// request could lease a billed worker while a local GPU sits idle.
    /// </summary>
    bool LocalBackendCanServe(T2IParamInput input)
    {
        string model = GetModelFromInput(input);
        HashSet<string> reasons = [.. input.RefusalReasons];
        try
        {
            foreach (BackendHandler.T2IBackendData data in Handler.EnumerateT2IBackends)
            {
                AbstractT2IBackend backend = data.Backend;
                if (backend is CloudBackendBase or CloudBackendsBackend || !backend.IsEnabled || backend.Status != BackendStatus.RUNNING || backend.MaxUsages <= 0 || data.CheckIsInUse)
                {
                    continue;
                }
                if (backend is OwnerBoundSwarmBackend bound && (bound.FindCloudRoot() is CloudBackendBase || bound.IsSpecialControlled && bound.Parent is null))
                {
                    continue;
                }
                if (model is not null && backend.Models is not null && backend.Models.TryGetValue("Stable-Diffusion", out List<string> names) && !names.Contains(model) && !names.Contains($"{model}.safetensors"))
                {
                    continue;
                }
                if (CanServe(data, input))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            // Asking other backends must not leave their refusal reasons on a request they were never offered.
            input.RefusalReasons.Clear();
            input.RefusalReasons.UnionWith(reasons);
        }
    }

    /// <summary>
    /// Whether a backend would accept this request, asked the way core would. A mirrored backend pins its remote
    /// backend by ID, so handing it a request it would refuse fails outright instead of being re-routed.
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

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override async Task Init()
    {
        AddLoadStatus($"Starting {GetType().Name} backend...");
        try
        {
            CheckRequiredConfig();
            // Every cloud backend runs on its owner's own key; there is deliberately no fallback to anyone else's.
            User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend has no valid owner user ('{OwnerUserId}').");
            ProviderApiKey = GetApiKey(owner);
        }
        catch (Exception ex)
        {
            AddLoadStatus($"ERROR: {ex.Message}");
            Status = BackendStatus.ERRORED;
            return;
        }
        Provider = CreateProvider(ProviderApiKey);
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
        LoadModelCache();
        // One usage per worker this backend may start. A request holds its usage for its whole generation (the
        // handoff runs inside it), so fewer than MaxWorkers would delay scale-out behind a running generation.
        MaxUsages = MaxWorkers;
        CanLoadModels = true;
        Status = BackendStatus.RUNNING;
        // A re-enable can Init without a matching Shutdown; never subscribe twice.
        Program.TickEvent -= OnTick;
        Program.TickEvent += OnTick;
        // Nothing is leased here: a backend that exists because its owner generated something never spends on its own.
        AddLoadStatus($"{Provider.ProviderName} backend ready (endpoint: {BaseConfig.EndpointId}, up to {MaxWorkers} worker(s)).");
    }

    public override async Task Shutdown()
    {
        Program.TickEvent -= OnTick;
        Logs.Info($"[{Provider?.ProviderName ?? GetType().Name}] Backend {BackendData?.ID} shutting down, releasing {Slots.Length} worker(s)...");
        foreach (WorkerSlot slot in Slots)
        {
            await RemoveSlotAsync(slot, "backend shutting down");
        }
        Provider?.Dispose();
        Provider = null;
        Status = BackendStatus.DISABLED;
    }

    public override IEnumerable<string> SupportedFeatures => RemoteFeatureCombo.IsEmpty ? ["text2image"] : RemoteFeatureCombo.Keys;

    /// <summary>
    /// Rebuilds the provider if the owner's API key changed, releasing every lease taken under the old key.
    /// Must be called under <see cref="SlotLock"/>.
    /// </summary>
    async Task RefreshProviderIfKeyChangedAsync()
    {
        User owner = Owner ?? throw new SwarmReadableErrorException($"Cloud backend's owner user ('{OwnerUserId}') no longer exists.");
        string currentKey = GetApiKey(owner);
        if (currentKey == ProviderApiKey)
        {
            return;
        }
        Logs.Info($"[{Provider?.ProviderName}] API key changed for user '{OwnerUserId}'; releasing workers and rebuilding the provider for backend #{BackendData?.ID}.");
        WorkerSlot[] old = Slots;
        Slots = [];
        foreach (WorkerSlot slot in old)
        {
            await DetachAndReleaseAsync(slot, "API key changed");
        }
        ICloudProvider oldProvider = Provider;
        Provider = CreateProvider(currentKey);
        ProviderApiKey = currentKey;
        oldProvider?.Dispose();
    }

    // ── Leasing workers ───────────────────────────────────────────────────────

    /// <summary>
    /// Leases one worker, waits for its SwarmUI backends, attaches the child, and adds the slot. If
    /// <paramref name="cancel"/> fires before the provider assigns a worker, the lease is withdrawn; after that the
    /// worker is kept as a slot, since it is already paid for.
    /// </summary>
    public async Task<WorkerSlot> AcquireSlotAsync(CancellationToken cancel)
    {
        Interlocked.Increment(ref PendingAcquires);
        CloudWorkerInfo worker = null;
        try
        {
            await SlotLock.WaitAsync(Program.GlobalProgramCancel);
            try
            {
                await RefreshProviderIfKeyChangedAsync();
            }
            finally
            {
                SlotLock.Release();
            }
            ICloudProvider provider = Provider ?? throw new SwarmReadableErrorException("This cloud backend is shutting down.");
            worker = await provider.AcquireWorkerAsync(MakeLeaseRequest(), cancel);
            WorkerSlot slot = new() { Worker = worker, ConnectUrl = await provider.GetConnectUrlAsync(worker) };
            await WaitForWorkerBackendsLoadedAsync(slot);
            slot.Child = CloudChildBackend.Attach(this, slot.ConnectUrl, $"[{provider.ProviderName} worker {worker.WorkerId}] Cloud Serverless", BaseConfig.StartupTimeoutSec, $"Bearer {worker.Token}");
            slot.NextLeaseCheckTick = Environment.TickCount64 + 15_000;
            await SlotLock.WaitAsync(Program.GlobalProgramCancel);
            try
            {
                Slots = [.. Slots, slot];
            }
            finally
            {
                SlotLock.Release();
            }
            Logs.Info($"[{provider.ProviderName}] Worker {worker.WorkerId} leased and attached as backend #{slot.Child.ID} ({Slots.Length} worker(s) now).");
            return slot;
        }
        catch (Exception) when (worker is not null)
        {
            await (Provider?.ReleaseLeaseAsync(worker) ?? Task.CompletedTask);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref PendingAcquires);
        }
    }

    /// <summary>
    /// Finds a mirrored backend to hand this request to, leasing another worker if every current one is busy and
    /// there is room. While a new worker is starting, a current worker that frees up first takes the request instead,
    /// and the new lease is withdrawn if it has not been assigned a worker yet.
    /// </summary>
    async Task<T2IBackendAccess> ClaimGeneratorAsync(T2IParamInput input)
    {
        LeaseRequest request = MakeLeaseRequest();
        DateTime deadline = DateTime.UtcNow.AddSeconds(request.StartupTimeoutSec + 60);
        using CancellationTokenSource withdraw = new();
        Task<WorkerSlot> acquiring = null;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                T2IBackendAccess access = await TryClaimAsync(input, allowBusy: acquiring is null && PendingAcquires == 0 && Slots.Length >= MaxWorkers);
                if (access is not null)
                {
                    return access;
                }
                if (acquiring is null && Slots.Length + PendingAcquires < MaxWorkers)
                {
                    acquiring = AcquireSlotAsync(withdraw.Token);
                }
                else if (acquiring is not null && acquiring.IsCompleted)
                {
                    // Surfaces a failed lease as this request's error. A good one is now a slot, found on the next pass.
                    await acquiring;
                    acquiring = null;
                }
                else if (acquiring is null && Slots.Length == 0 && PendingAcquires == 0)
                {
                    throw new SwarmReadableErrorException($"{CloudProviderName ?? "Cloud"} could not get a worker for this request.");
                }
                await Task.Delay(request.PollIntervalMs, Program.GlobalProgramCancel);
            }
            throw new SwarmReadableErrorException($"{CloudProviderName ?? "Cloud"} had no worker able to take this request within {request.StartupTimeoutSec}s.");
        }
        finally
        {
            if (acquiring is not null && !acquiring.IsCompleted)
            {
                withdraw.Cancel();
                // Let it finish in the background: withdrawn, or kept as a slot if a worker was already assigned.
                _ = acquiring.ContinueWith(t => Logs.Debug($"[{CloudProviderName}] Spare lease ended: {t.Exception?.InnerException?.Message ?? "kept as a worker"}"), TaskScheduler.Default);
            }
        }
    }

    /// <summary>Picks and claims a mirrored backend that can serve the request, atomically. Null if none is available.</summary>
    async Task<T2IBackendAccess> TryClaimAsync(T2IParamInput input, bool allowBusy)
    {
        await SlotLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            List<BackendHandler.T2IBackendData> candidates = [.. Slots.SelectMany(s => s.RunningGrandchildren).Where(d => CanServe(d, input))];
            BackendHandler.T2IBackendData pick = candidates.Where(d => !d.CheckIsInUse).OrderBy(d => d.Usages).FirstOrDefault();
            // At capacity, queue on the least-used worker backend rather than wait here: its own queue is the right place.
            if (pick is null && allowBusy)
            {
                pick = candidates.OrderBy(d => d.Usages).FirstOrDefault();
            }
            return pick is null ? null : new T2IBackendAccess(pick);
        }
        finally
        {
            SlotLock.Release();
        }
    }

    // ── Releasing workers ─────────────────────────────────────────────────────

    /// <summary>Removes a slot: keeps its model list, detaches its child, and ends its lease.</summary>
    public async Task RemoveSlotAsync(WorkerSlot slot, string reason)
    {
        await SlotLock.WaitAsync(CancellationToken.None);
        try
        {
            if (!Slots.Contains(slot))
            {
                return;
            }
            Slots = [.. Slots.Where(s => s != slot)];
        }
        finally
        {
            SlotLock.Release();
        }
        await DetachAndReleaseAsync(slot, reason);
    }

    /// <summary>Detach and release, for a slot already taken out of <see cref="Slots"/>.</summary>
    async Task DetachAndReleaseAsync(WorkerSlot slot, string reason)
    {
        slot.Removing = true;
        AdoptModelLists(slot);
        await CloudChildBackend.DetachAsync(Handler, slot.Child, Provider?.ProviderName);
        await (Provider?.ReleaseLeaseAsync(slot.Worker) ?? Task.CompletedTask);
        Logs.Info($"[{Provider?.ProviderName}] Released worker {slot.Worker.WorkerId} ({reason}); {Slots.Length} worker(s) left.");
    }

    /// <summary>Finds a slot by the worker ID the provider reported.</summary>
    public WorkerSlot FindSlot(string workerId)
    {
        return Slots.FirstOrDefault(s => s.Worker.WorkerId == workerId);
    }

    /// <summary>
    /// Every few seconds: renews leases in use (only Vast.ai needs it) and drops slots whose lease has ended, so the
    /// backend list never keeps a subtree pointed at a worker that is gone.
    /// </summary>
    void OnTick()
    {
        if (Environment.TickCount64 < NextTick || Slots.Length == 0)
        {
            return;
        }
        NextTick = Environment.TickCount64 + 5000;
        if (Interlocked.Exchange(ref TickRunning, 1) == 1)
        {
            return;
        }
        Utilities.RunCheckedTask(async () =>
        {
            try
            {
                await MaintainSlotsAsync();
            }
            finally
            {
                Interlocked.Exchange(ref TickRunning, 0);
            }
        }, $"{CloudProviderName} worker maintenance");
    }

    /// <summary>The work behind <see cref="OnTick"/>.</summary>
    async Task MaintainSlotsAsync()
    {
        ICloudProvider provider = Provider;
        if (provider is null)
        {
            return;
        }
        long now = Environment.TickCount64;
        foreach (WorkerSlot slot in Slots)
        {
            if (slot.InUse && now >= slot.NextRenewTick)
            {
                slot.NextRenewTick = now + 15_000;
                await RenewQuietlyAsync(provider, slot);
            }
            if (now < slot.NextLeaseCheckTick)
            {
                continue;
            }
            slot.NextLeaseCheckTick = now + 15_000;
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            if (!await provider.IsLeaseActiveAsync(slot.Worker, cancel.Token))
            {
                await RemoveSlotAsync(slot, "lease ended");
            }
        }
    }

    /// <summary>Renews a lease, logging rather than throwing on failure (the lease check will notice a real loss).</summary>
    static async Task RenewQuietlyAsync(ICloudProvider provider, WorkerSlot slot)
    {
        try
        {
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
            await provider.RenewLeaseAsync(slot.Worker, cancel.Token);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[{provider.ProviderName}] Renewing worker {slot.Worker.WorkerId} failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    /// <remarks>Renews in-use leases immediately rather than on the next tick. Never leases anything.</remarks>
    public async Task OnChildGenerationStartingAsync()
    {
        ICloudProvider provider = Provider;
        if (provider is null)
        {
            return;
        }
        foreach (WorkerSlot slot in Slots)
        {
            await RenewQuietlyAsync(provider, slot);
        }
    }

    // ── Talking to a worker ───────────────────────────────────────────────────

    /// <summary>POSTs to a leased worker's SwarmUI API through its gateway, with the lease token and a SwarmUI session.</summary>
    /// <param name="slot">The leased worker.</param>
    /// <param name="apiPath">SwarmUI API route name, e.g. "ListBackends".</param>
    /// <param name="body">Request body; <c>session_id</c> is added.</param>
    /// <param name="timeoutSeconds">Whole-call timeout.</param>
    /// <param name="retriedSession">Internal: true on the one retry after the worker dropped our SwarmUI session.</param>
    public async Task<JObject> CallWorkerAPI(WorkerSlot slot, string apiPath, JObject body, int timeoutSeconds = 120, bool retriedSession = false)
    {
        CloudWorkerInfo worker = slot.Worker;
        if (worker.SessionId is null && apiPath != "GetNewSession")
        {
            JObject session = await CallWorkerAPI(slot, "GetNewSession", [], timeoutSeconds);
            worker.SessionId = session["session_id"]?.ToString();
        }
        body = (JObject)body.DeepClone();
        if (apiPath != "GetNewSession")
        {
            body["session_id"] = worker.SessionId;
        }
        string url = $"{slot.ConnectUrl}/API/{apiPath}";
        using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        JObject result = await HttpClient.PostJson(url, body, req => req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", worker.Token), cancel.Token);
        if (result.TryGetValue("error_id", out JToken errorId))
        {
            if (errorId.ToString() == "invalid_session_id" && !retriedSession)
            {
                worker.SessionId = null;
                return await CallWorkerAPI(slot, apiPath, body, timeoutSeconds, true);
            }
            if (errorId.ToString() == "worker_starting")
            {
                throw new WorkerNotReadyException();
            }
            if (errorId.ToString() == "worker_unauthorized")
            {
                throw new SwarmReadableErrorException($"{CloudProviderName} worker {worker.WorkerId} refused its lease token; the lease has ended.");
            }
        }
        if (result.TryGetValue("error", out JToken error))
        {
            throw new SwarmReadableErrorException($"{CloudProviderName} worker gave error: {error}");
        }
        return result;
    }

    /// <summary>The worker's gateway is up but its SwarmUI is still starting; worth retrying.</summary>
    public class WorkerNotReadyException : Exception
    {
    }

    /// <summary>
    /// Waits until the worker's SwarmUI has at least one backend running and none still loading. A worker that
    /// answers but has no backend at all is a broken image, reported promptly rather than after the full timeout.
    /// </summary>
    async Task WaitForWorkerBackendsLoadedAsync(WorkerSlot slot)
    {
        LeaseRequest request = MakeLeaseRequest();
        DateTime start = DateTime.UtcNow;
        bool everSawBackend = false;
        string lastError = null;
        while ((DateTime.UtcNow - start).TotalSeconds < request.StartupTimeoutSec)
        {
            try
            {
                JObject data = await CallWorkerAPI(slot, "ListBackends", new JObject { ["nonreal"] = true, ["full_data"] = true }, 30);
                JObject[] backends = [.. data.Properties().Select(p => p.Value).OfType<JObject>()];
                everSawBackend |= backends.Length > 0;
                UpdateFeaturesFromWorker(data);
                bool anyRunning = backends.Any(b => b["status"]?.ToString() == "running");
                bool anyLoading = backends.Any(b => b["status"]?.ToString() == "loading");
                if (anyRunning && !anyLoading)
                {
                    return;
                }
                if (!everSawBackend && (DateTime.UtcNow - start).TotalSeconds > 90)
                {
                    throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} is running SwarmUI with no backend configured, so it cannot generate. Check the worker image.");
                }
            }
            catch (SwarmReadableErrorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A freshly assigned worker's proxy often answers nothing for a few seconds.
                lastError = ex.Message;
            }
            await RenewQuietlyAsync(Provider, slot);
            await Task.Delay(request.PollIntervalMs, Program.GlobalProgramCancel);
        }
        throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} did not bring up a usable backend within {request.StartupTimeoutSec}s{(lastError is null ? "." : $" (last error: {lastError}).")}");
    }

    /// <summary>Syncs <see cref="RemoteFeatureCombo"/> from a worker's ListBackends response.</summary>
    void UpdateFeaturesFromWorker(JObject backendData)
    {
        HashSet<string> features = ["text2image"];
        foreach (JToken backend in backendData.Values())
        {
            if (backend["status"]?.ToString() is "running" && backend["features"] is JArray arr)
            {
                features.UnionWith(arr.Select(f => f.ToString()));
            }
        }
        foreach (string f in features)
        {
            RemoteFeatureCombo.TryAdd(f, f);
        }
    }

    // ── Models ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Never leases anything. With leases, the worker's own backend loads the model as part of the generation it is
    /// handed, so there is nothing to do here, and waking a billed worker just to "load" would be wasted money.
    /// </remarks>
    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        CurrentModelName = model?.Name;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Copies a slot's mirrored model lists onto this backend (merged with what is already known), and saves them.
    /// That is what lets a sleeping endpoint still offer its models, and core refuse models it does not have.
    /// </summary>
    void AdoptModelLists(WorkerSlot slot)
    {
        if (slot.Child?.AbstractBackend is not SwarmSwarmBackend swarm || swarm.RemoteModels is null || !swarm.RemoteModels.Any(kv => kv.Value.Count > 0))
        {
            return;
        }
        CommitModels(swarm.RemoteModels);
    }

    /// <summary>Merges a model listing into <see cref="RemoteModels"/> and <see cref="AbstractT2IBackend.Models"/>, and persists it.</summary>
    void CommitModels(IEnumerable<KeyValuePair<string, Dictionary<string, JObject>>> listing)
    {
        RemoteModels ??= new();
        Models ??= new();
        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in listing)
        {
            Dictionary<string, JObject> merged = RemoteModels.TryGetValue(kv.Key, out Dictionary<string, JObject> existing) ? new(existing) : [];
            foreach (KeyValuePair<string, JObject> model in kv.Value)
            {
                merged[model.Key] = model.Value;
            }
            RemoteModels[kv.Key] = merged;
            Models[kv.Key] = [.. merged.Keys];
        }
        SaveModelCache();
        Program.ModelRefreshEvent?.Invoke();
    }

    /// <summary>Per-user storage key for this endpoint's model list.</summary>
    string ModelCacheKey => $"models_{HandlerTypeData?.ID}_{BaseConfig.EndpointId}".ToLowerFast();

    /// <summary>Metadata fields worth keeping across restarts. Everything else (previews especially) is rebuilt or dropped.</summary>
    static readonly string[] CachedFields = ["name", "title", "architecture", "class", "standard_width", "standard_height"];

    /// <summary>Saves the model list for this user and endpoint.</summary>
    void SaveModelCache()
    {
        User owner = Owner;
        if (owner is null || RemoteModels is null)
        {
            return;
        }
        JObject data = [];
        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in RemoteModels)
        {
            JObject subtype = [];
            foreach (KeyValuePair<string, JObject> model in kv.Value)
            {
                JObject slim = [];
                foreach (string field in CachedFields.Where(f => model.Value[f] is not null))
                {
                    slim[field] = model.Value[field];
                }
                subtype[model.Key] = slim;
            }
            data[kv.Key] = subtype;
        }
        owner.SaveGenericData("cloudbackends", ModelCacheKey, data.ToString(Newtonsoft.Json.Formatting.None));
    }

    /// <summary>Loads the saved model list, if any. A damaged entry is ignored and the list stays unknown.</summary>
    void LoadModelCache()
    {
        string raw = Owner?.GetGenericData("cloudbackends", ModelCacheKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        try
        {
            ConcurrentDictionary<string, Dictionary<string, JObject>> loaded = new();
            foreach (JProperty subtype in JObject.Parse(raw).Properties())
            {
                Dictionary<string, JObject> models = [];
                foreach (JProperty model in ((JObject)subtype.Value).Properties())
                {
                    JObject meta = (JObject)model.Value;
                    meta["name"] ??= model.Name;
                    meta["title"] ??= model.Name.AfterLast('/');
                    meta["local"] = false;
                    meta["preview_image"] = "imgs/model_placeholder.jpg";
                    meta["is_supported_model_format"] = true;
                    models[model.Name] = meta;
                }
                loaded[subtype.Name] = models;
            }
            RemoteModels = loaded;
            Models = new(loaded.ToDictionary(kv => kv.Key, kv => kv.Value.Keys.ToList()));
            Logs.Debug($"[{GetType().Name}] Loaded {loaded.Values.Sum(v => v.Count)} remembered model(s) for endpoint '{BaseConfig.EndpointId}'.");
        }
        catch (Exception ex)
        {
            Logs.Verbose($"[{GetType().Name}] Ignoring a damaged saved model list: {ex.Message}");
        }
    }

    /// <summary>Forgets the saved model list (the CloudClearModelCache route).</summary>
    public void ClearModelCache()
    {
        Owner?.DeleteGenericData("cloudbackends", ModelCacheKey);
        RemoteModels = null;
        Models = null;
        Program.ModelRefreshEvent?.Invoke();
    }

    /// <summary>
    /// Discovers the endpoint's models (the CloudRefreshModels route): uses a leased worker if there is one, or leases
    /// one, which bills. The worker then goes idle and is released as usual.
    /// </summary>
    public async Task RefreshModelsFromWorkerAsync()
    {
        WorkerSlot slot = Slots.FirstOrDefault() ?? await AcquireSlotAsync(CancellationToken.None);
        ConcurrentDictionary<string, Dictionary<string, JObject>> listing = new();
        foreach (string subtype in Program.T2IModelSets.Keys)
        {
            JObject response = await CallWorkerAPI(slot, "ListModels", new JObject
            {
                ["path"] = "",
                ["depth"] = 999,
                ["subtype"] = subtype,
                ["allowRemote"] = false,
                ["dataImages"] = false
            });
            Dictionary<string, JObject> models = [];
            foreach (JToken file in response["files"] as JArray ?? [])
            {
                JObject meta = file is JObject obj ? (JObject)obj.DeepClone() : new JObject { ["name"] = file.ToString() };
                string name = meta["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                meta["local"] = false;
                meta["title"] ??= name.AfterLast('/');
                meta["preview_image"] ??= "imgs/model_placeholder.jpg";
                meta["is_supported_model_format"] ??= true;
                models[name] = meta;
            }
            listing[subtype] = models;
        }
        if (listing.Values.Sum(m => m.Count) == 0)
        {
            throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} has no models. Check that its models are where the worker image looks for them.");
        }
        CommitModels(listing);
        Logs.Info($"[{CloudProviderName}] Discovered {listing.Values.Sum(m => m.Count)} model(s) on endpoint '{BaseConfig.EndpointId}'.");
    }

    // ── Status ────────────────────────────────────────────────────────────────

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
            ["workers"] = Slots.Length,
            ["workers_starting"] = PendingAcquires,
            ["max_workers"] = MaxWorkers
        };
    }

    /// <summary>Per-worker detail for the CloudListWorkers route. Never includes tokens.</summary>
    public JArray DescribeWorkers()
    {
        JArray workers = [];
        foreach (WorkerSlot slot in Slots)
        {
            workers.Add(new JObject
            {
                ["worker_id"] = slot.Worker.WorkerId,
                ["lease_id"] = slot.Worker.LeaseId,
                ["public_url"] = slot.Worker.PublicUrl,
                ["age_seconds"] = (int)(DateTime.UtcNow - slot.StartedUtc).TotalSeconds,
                ["in_use"] = slot.InUse,
                ["child_backend_id"] = slot.Child?.ID,
                ["backends"] = new JArray(slot.Grandchildren.Select(d => new JObject
                {
                    ["id"] = d.ID,
                    ["status"] = d.Backend.Status.ToString().ToLowerFast(),
                    ["usages"] = d.Usages,
                    ["max_usages"] = d.Backend.MaxUsages
                }))
            });
        }
        return workers;
    }

    /// <summary>Checks the endpoint's provider-side configuration (the CloudValidateBackend route).</summary>
    public async Task<JArray> CheckConfigurationAsync(CancellationToken cancel)
    {
        ICloudProvider provider = Provider ?? throw new SwarmReadableErrorException($"This backend is not running ({Status}).");
        JArray findings = await provider.CheckEndpointAsync(MakeLeaseRequest(), MaxWorkers, cancel);
        if (BaseConfig.IdleSeconds < 30)
        {
            findings.Add(new JObject { ["level"] = "warning", ["message"] = $"Idle Seconds is {BaseConfig.IdleSeconds}; workers will often shut down between generations and cold-start again." });
        }
        return findings;
    }

    // ── Generation ────────────────────────────────────────────────────────────

    /// <remarks>
    /// Only reached when no leased worker's backend could take the request (see <see cref="IsValidForThisBackend"/>).
    /// Hands it to a worker backend, leasing a worker first if needed. The worker backend is claimed properly, so a
    /// second request arriving mid-generation sees it busy rather than double-booking it.
    /// </remarks>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        if (user_input.SourceSession is not null)
        {
            CheckPermission(user_input.SourceSession);
        }
        using T2IBackendAccess access = await ClaimGeneratorAsync(user_input);
        return await access.Backend.Generate(user_input);
    }

    /// <inheritdoc cref="Generate"/>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        if (user_input.SourceSession is not null)
        {
            CheckPermission(user_input.SourceSession);
        }
        using T2IBackendAccess access = await ClaimGeneratorAsync(user_input);
        await access.Backend.GenerateLive(user_input, batchId, takeOutput);
    }

    /// <summary>The main model a request names, or null.</summary>
    static string GetModelFromInput(T2IParamInput input)
    {
        if (input is null)
        {
            return null;
        }
        object model = input.Get(T2IParamTypes.Model);
        return model is T2IModel typed ? typed.Name : model as string;
    }
}
