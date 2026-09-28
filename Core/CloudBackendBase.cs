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
    public ICloudProvider Provider;

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

    /// <summary>Leases being acquired right now. Changed only under <see cref="ReserveLock"/>.</summary>
    int PendingAcquires = 0;

    /// <summary>Makes the capacity check and the reservation of a new lease one step, so two callers can never both take the last place.</summary>
    readonly object ReserveLock = new();

    /// <summary>Set when shutdown begins. Leases that complete afterwards release themselves instead of attaching.</summary>
    volatile bool ShuttingDown = false;

    /// <summary>Cancelled when shutdown begins, withdrawing leases the provider has not assigned a worker to yet.</summary>
    CancellationTokenSource Lifetime = new();

    /// <summary>Serializes model discovery, so concurrent requests share one worker instead of each leasing their own.</summary>
    readonly SemaphoreSlim DiscoveryLock = new(1, 1);

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

    /// <summary><see cref="BaseSettings.MaxWorkers"/>, within sane bounds and the provider's own limit.</summary>
    public int MaxWorkers => Math.Clamp(Math.Min(BaseConfig.MaxWorkers, Provider?.WorkerLimit() ?? int.MaxValue), 1, 64);

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
        if (!IsAnyBackendType(input))
        {
            // Explicitly targeted at this type: core can then only route here, never to the workers' mirrored
            // backends (they are another type), so this backend takes it and hands it over itself.
            return true;
        }
        if (LocalBackendCanServe(input))
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
        return ShouldTakeRequest(slots.Length, PendingAcquires, MaxWorkers, slots.Any(s => s.RunningGrandchildren.Any()));
    }

    /// <summary>
    /// Whether this backend should take a request that no free worker backend can serve. At capacity with workers
    /// up, the request waits on their backends instead of starting another lease. Before any worker is up it is
    /// taken anyway, so it queues here rather than failing for lack of a candidate; the lease underway serves it.
    /// </summary>
    internal static bool ShouldTakeRequest(int slots, int pendingAcquires, int maxWorkers, bool anyWorkerRunning)
    {
        return !(anyWorkerRunning && slots + pendingAcquires >= maxWorkers);
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
                // Only defer to a backend known to have the model; one that cannot say might fail to load it.
                if (model is not null && (backend.Models is null || !backend.Models.TryGetValue("Stable-Diffusion", out List<string> names) || (!names.Contains(model) && !names.Contains($"{model}.safetensors"))))
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
        ShuttingDown = false;
        Lifetime.Dispose();
        Lifetime = new CancellationTokenSource();
        LoadModelCache();
        // A request holds a usage here for its whole handed-off generation, so this must not be the bottleneck:
        // leasing itself is limited by MaxWorkers (slots plus leases starting), not by this.
        MaxUsages = Math.Max(4, MaxWorkers * 4);
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
        ShuttingDown = true;
        Program.TickEvent -= OnTick;
        Logs.Info($"[{Provider?.ProviderName ?? GetType().Name}] Backend {BackendData?.ID} shutting down, releasing {Slots.Length} worker(s)...");
        // Withdraw leases still queued, and give ones already assigned a worker a moment to release themselves
        // (AcquireSlotAsync does that when it sees ShuttingDown) while the provider still exists.
        Lifetime.Cancel();
        for (int i = 0; i < 120 && PendingAcquires > 0; i++)
        {
            await Task.Delay(500);
        }
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
    /// Reserves room for one more lease if <see cref="MaxWorkers"/> allows it. Every call that returns true must be
    /// followed by exactly one <see cref="AcquireSlotAsync"/>, which releases the reservation when it finishes.
    /// </summary>
    internal bool TryReserveLease()
    {
        lock (ReserveLock)
        {
            if (ShuttingDown || Slots.Length + PendingAcquires >= MaxWorkers)
            {
                return false;
            }
            PendingAcquires++;
            return true;
        }
    }

    /// <summary>
    /// Leases one worker (after <see cref="TryReserveLease"/>), waits for its SwarmUI backends, attaches the child, and
    /// adds the slot. If <paramref name="cancel"/> fires, or shutdown begins, before the provider assigns a worker, the
    /// lease is withdrawn; after that the worker is kept as a slot, since it is already paid for, unless the backend is
    /// shutting down, in which case it is released.
    /// </summary>
    public async Task<WorkerSlot> AcquireSlotAsync(CancellationToken cancel)
    {
        CloudWorkerInfo worker = null;
        ICloudProvider provider = null;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, Lifetime.Token);
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
            provider = Provider ?? throw new SwarmReadableErrorException("This cloud backend is shutting down.");
            worker = await provider.AcquireWorkerAsync(MakeLeaseRequest(), linked.Token);
            WorkerSlot slot = new() { Worker = worker, Provider = provider, ConnectUrl = await provider.GetConnectUrlAsync(worker) };
            await WaitForWorkerBackendsLoadedAsync(slot, provider);
            slot.Child = CloudChildBackend.Attach(this, slot.ConnectUrl, $"[{provider.ProviderName} worker {worker.WorkerId}] Cloud Serverless", BaseConfig.StartupTimeoutSec, worker.Token is null ? null : $"Bearer {worker.Token}");
            slot.NextLeaseCheckTick = Environment.TickCount64 + 15_000;
            bool added = false;
            await SlotLock.WaitAsync(CancellationToken.None);
            try
            {
                // Checked under the same lock Shutdown's removal and a key change's release use, so a worker is either
                // added before those release every slot, or never added at all. A lease from a provider that has since
                // been replaced (the owner changed their API key mid-start) belongs to the old key's account: release it.
                if (!ShuttingDown && ReferenceEquals(Provider, provider))
                {
                    Slots = [.. Slots, slot];
                    added = true;
                }
            }
            finally
            {
                SlotLock.Release();
            }
            if (!added)
            {
                await CloudChildBackend.DetachAsync(Handler, slot.Child, provider.ProviderName);
                throw new SwarmReadableErrorException("This cloud backend shut down, or its API key changed, while a worker was starting; the worker was released.");
            }
            Logs.Info($"[{provider.ProviderName}] Worker {worker.WorkerId} leased and attached as backend #{slot.Child.ID} ({Slots.Length} worker(s) now).");
            return slot;
        }
        catch (Exception) when (worker is not null)
        {
            // The provider this lease came from, even if the backend has since dropped or replaced its own.
            await provider.ReleaseLeaseAsync(worker);
            throw;
        }
        finally
        {
            lock (ReserveLock)
            {
                PendingAcquires--;
            }
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
        DateTime? refusedSince = null;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                T2IBackendAccess access = await TryClaimAsync(input, allowBusy: acquiring is null && PendingAcquires == 0 && Slots.Length >= MaxWorkers);
                if (access is not null)
                {
                    return access;
                }
                // An idle worker that refuses the request will keep refusing it; say why instead of waiting out the timeout.
                string refusal = RefusalReason(input);
                refusedSince = refusal is null ? null : refusedSince ?? DateTime.UtcNow;
                if (refusedSince is DateTime since && (DateTime.UtcNow - since).TotalSeconds > 30)
                {
                    throw new SwarmReadableErrorException($"{CloudProviderName ?? "Cloud"} workers cannot run this request: {refusal}");
                }
                if (acquiring is null && TryReserveLease())
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
    /// <summary>
    /// Why the attached workers refuse a request, when at least one of their backends is running and idle and none will take
    /// it; null when some could, or when every backend is simply busy or starting (worth waiting for).
    /// </summary>
    string RefusalReason(T2IParamInput input)
    {
        BackendHandler.T2IBackendData[] idle = [.. Slots.SelectMany(s => s.RunningGrandchildren).Where(d => !d.CheckIsInUse)];
        if (idle.Length == 0 || idle.Any(d => CanServe(d, input)))
        {
            return null;
        }
        HashSet<string> missing = [.. idle.SelectMany(d => input.RequiredFlags.Where(f => !d.Backend.SupportedFeatures.Contains(f) && !T2IEngine.DisregardedFeatureFlags.Contains(f)))];
        string reason = missing.Count > 0
            ? $"their backends do not support {string.Join(", ", missing.Order())}. The worker image may be too old for this model or feature."
            : "their backends do not accept this model or its parameters. Check the model exists on the endpoint, or press Discover models.";
        Logs.Debug($"[{CloudProviderName}] Idle worker backends refuse request: {reason}");
        return reason;
    }

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
        // Copied before detaching (the lists go with the child), saved after releasing: saving triggers a model refresh that
        // would reach this worker, whose token is already revoked when its lease ended, and must never stop the release.
        List<KeyValuePair<string, Dictionary<string, JObject>>> models = ModelListsOf(slot);
        await CloudChildBackend.DetachAsync(Handler, slot.Child, slot.Provider.ProviderName);
        await slot.Provider.ReleaseLeaseAsync(slot.Worker);
        Logs.Info($"[{slot.Provider.ProviderName}] Released worker {slot.Worker.WorkerId} ({reason}); {Slots.Length} worker(s) left.");
        if (models is not null)
        {
            CommitModels(models);
        }
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
                await RenewQuietlyAsync(slot.Provider, slot);
            }
            if (now < slot.NextLeaseCheckTick)
            {
                continue;
            }
            slot.NextLeaseCheckTick = now + 15_000;
            try
            {
                using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromSeconds(30));
                if (!await slot.Provider.IsLeaseActiveAsync(slot.Worker, cancel.Token))
                {
                    await RemoveSlotAsync(slot, "lease ended");
                }
            }
            catch (Exception ex)
            {
                // One worker's unexpected failure must not stop the others from being checked.
                Logs.Warning($"[{slot.Provider.ProviderName}] Checking worker {slot.Worker.WorkerId} failed: {ex.Message}");
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
    /// <remarks>Renews the lease of the one worker starting the generation, right away rather than on the next tick.
    /// Renewing other workers here would keep idle ones alive (and billing) on another worker's traffic. Never leases anything.</remarks>
    public async Task OnChildGenerationStartingAsync(SwarmSwarmBackend control)
    {
        WorkerSlot slot = Slots.FirstOrDefault(s => ReferenceEquals(s.Child?.AbstractBackend, control));
        if (slot is null)
        {
            return;
        }
        await RenewQuietlyAsync(slot.Provider, slot);
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
        JObject result = await HttpClient.PostJson(url, body, worker.Token is null ? null : req => req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", worker.Token), cancel.Token);
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
    /// <summary>The worker's own latest error log lines, so a failed start can say why. Null if they cannot be read.</summary>
    async Task<string> RecentWorkerErrorsAsync(WorkerSlot slot)
    {
        try
        {
            JObject logs = await CallWorkerAPI(slot, "ListRecentLogMessages", new JObject { ["types"] = new JArray("Error"), ["last_sequence_ids"] = new JObject() }, 20);
            string[] lines = [.. (logs["data"]?["Error"] as JArray ?? []).TakeLast(2).Select(m => m["message"]?.ToString()).Where(m => !string.IsNullOrWhiteSpace(m))];
            if (lines.Length == 0)
            {
                return null;
            }
            string joined = string.Join(" | ", lines);
            return joined.Length > 600 ? joined[..600] + "..." : joined;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[{CloudProviderName}] Could not read worker {slot.Worker.WorkerId}'s logs: {ex.Message}");
            return null;
        }
    }

    async Task WaitForWorkerBackendsLoadedAsync(WorkerSlot slot, ICloudProvider provider)
    {
        LeaseRequest request = MakeLeaseRequest();
        DateTime start = DateTime.UtcNow;
        bool everSawBackend = false;
        bool everAnswered = false;
        string lastError = null;
        string lastState = null;
        DateTime? allFailedSince = null;
        while ((DateTime.UtcNow - start).TotalSeconds < request.StartupTimeoutSec)
        {
            if (ShuttingDown || !ReferenceEquals(Provider, provider))
            {
                // Shut down, or the owner's API key changed and the provider this lease came from was replaced (for
                // Vast.ai that also closed its TLS relay). Stop waiting; the caller releases the lease on its account.
                throw new SwarmReadableErrorException("This cloud backend shut down, or its API key changed, while a worker was starting; the worker was released.");
            }
            try
            {
                JObject data = await CallWorkerAPI(slot, "ListBackends", new JObject { ["nonreal"] = true, ["full_data"] = true }, 30);
                everAnswered = true;
                JObject[] backends = [.. data.Properties().Select(p => p.Value).OfType<JObject>()];
                everSawBackend |= backends.Length > 0;
                UpdateFeaturesFromWorker(data);
                string state = string.Join(", ", backends.Select(b => $"{b["type"]}={b["status"]}"));
                if (state != lastState)
                {
                    Logs.Debug($"[{CloudProviderName}] Worker {slot.Worker.WorkerId} backends: {(state.Length == 0 ? "none yet" : state)}");
                    lastState = state;
                }
                bool anyRunning = backends.Any(b => b["status"]?.ToString() == "running");
                bool anyLoading = backends.Any(b => b["status"]?.ToString() == "loading");
                if (anyRunning && !anyLoading)
                {
                    return;
                }
                // Every backend failed to start: waiting out the startup timeout will not change that.
                bool allFailed = backends.Length > 0 && backends.All(b => b["status"]?.ToString() is "errored" or "disabled");
                allFailedSince = allFailed ? allFailedSince ?? DateTime.UtcNow : null;
                if (allFailedSince is DateTime since && (DateTime.UtcNow - since).TotalSeconds > 60)
                {
                    string cause = await RecentWorkerErrorsAsync(slot);
                    throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} could not start its generation backend ({state}).{(cause is null ? " Check the worker's logs in the provider console." : $" The worker reported: {cause}")}");
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
            catch (WorkerNotReadyException)
            {
                // The worker's gateway answered: it is reachable, and its SwarmUI is still starting.
                if (!everAnswered)
                {
                    Logs.Debug($"[{CloudProviderName}] Worker {slot.Worker.WorkerId} is reachable; its SwarmUI is still starting.");
                }
                everAnswered = true;
            }
            catch (Exception ex)
            {
                // A freshly assigned worker's proxy often answers nothing for a few seconds.
                if (ex.Message != lastError)
                {
                    Logs.Debug($"[{CloudProviderName}] Worker {slot.Worker.WorkerId} not answering yet: {ex.Message}");
                }
                lastError = ex.Message;
                // The worker's gateway answers as soon as it runs, even while SwarmUI starts, so minutes of silence mean the address is unreachable.
                if (!everAnswered && (DateTime.UtcNow - start).TotalSeconds > 180)
                {
                    throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} was assigned but never answered at {slot.ConnectUrl} (last error: {lastError}). On RunPod, the endpoint must expose port 7801 as HTTP; Validate checks this.");
                }
            }
            await RenewQuietlyAsync(provider, slot);
            await Task.Delay(request.PollIntervalMs, Program.GlobalProgramCancel);
        }
        throw new SwarmReadableErrorException($"{CloudProviderName} worker {slot.Worker.WorkerId} did not bring up a usable backend within {request.StartupTimeoutSec}s{(lastError is null ? "." : $" (last error: {lastError}).")}");
    }

    /// <summary>
    /// Replaces <see cref="RemoteFeatureCombo"/> with what a worker's ListBackends response advertises. Every worker of an
    /// endpoint runs the same image, so the newest report is authoritative: a feature the workers dropped (after an image
    /// change) stops being advertised, instead of routing requests here that no worker can serve. The set is kept while
    /// no worker runs, so a sleeping endpoint still advertises what its workers last did.
    /// </summary>
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
        foreach (string f in RemoteFeatureCombo.Keys.Where(f => !features.Contains(f)))
        {
            RemoteFeatureCombo.TryRemove(f, out _);
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
    /// A copy of a slot's mirrored model lists, or null if it has none. Kept on this backend after the worker goes, which is
    /// what lets a sleeping endpoint still offer its models, and core refuse models it does not have.
    /// </summary>
    List<KeyValuePair<string, Dictionary<string, JObject>>> ModelListsOf(WorkerSlot slot)
    {
        if (slot.Child?.AbstractBackend is not SwarmSwarmBackend swarm || swarm.RemoteModels is null || !swarm.RemoteModels.Any(kv => kv.Value.Count > 0))
        {
            return null;
        }
        return [.. swarm.RemoteModels.Select(kv => new KeyValuePair<string, Dictionary<string, JObject>>(kv.Key, kv.Value.ToDictionary(m => m.Key, m => (JObject)m.Value.DeepClone())))];
    }

    /// <summary>
    /// Stores a model listing in <see cref="RemoteModels"/> and <see cref="AbstractT2IBackend.Models"/>, and persists it.
    /// Each reported subtype's list is complete, so it replaces the old one: models removed from the endpoint drop out.
    /// </summary>
    void CommitModels(IEnumerable<KeyValuePair<string, Dictionary<string, JObject>>> listing)
    {
        RemoteModels ??= new();
        Models ??= new();
        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in listing)
        {
            Dictionary<string, JObject> models = kv.Value.ToDictionary(m => m.Key, m => WithModelDefaults(m.Value, m.Key));
            RemoteModels[kv.Key] = models;
            Models[kv.Key] = [.. models.Keys];
        }
        SaveModelCache();
        try
        {
            Program.ModelRefreshEvent?.Invoke();
        }
        catch (Exception ex)
        {
            // Refreshing reaches every backend, including workers that just went away; the list itself is already saved.
            Logs.Debug($"[{CloudProviderName}] Model refresh after updating the model list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks a cloud model as remote and fills every field core's <see cref="T2IModel.FromNetObject"/> reads without a fallback
    /// (loaded, standard_width, standard_height): one missing field there breaks the model list for the whole server.
    /// </summary>
    internal static JObject WithModelDefaults(JObject meta, string name)
    {
        meta["name"] ??= name;
        meta["title"] ??= name.AfterLast('/');
        meta["description"] ??= "";
        meta["local"] = false;
        meta["preview_image"] ??= "imgs/model_placeholder.jpg";
        meta["is_supported_model_format"] ??= true;
        if (meta["loaded"]?.Type != JTokenType.Boolean)
        {
            meta["loaded"] = false;
        }
        foreach (string dimension in new[] { "standard_width", "standard_height" })
        {
            if (meta[dimension]?.Type != JTokenType.Integer)
            {
                meta[dimension] = 0;
            }
        }
        return meta;
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
        owner.SaveGenericData("cloudbackends", ModelCacheKey, SerializeModelCache(RemoteModels));
    }

    /// <summary>A compact form of a model list: names plus a few small fields, never previews.</summary>
    internal static string SerializeModelCache(IEnumerable<KeyValuePair<string, Dictionary<string, JObject>>> models)
    {
        JObject data = [];
        foreach (KeyValuePair<string, Dictionary<string, JObject>> kv in models)
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
        return data.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Rebuilds a model list saved by <see cref="SerializeModelCache"/>, with the placeholder fields the UI expects.</summary>
    internal static ConcurrentDictionary<string, Dictionary<string, JObject>> ParseModelCache(string raw)
    {
        ConcurrentDictionary<string, Dictionary<string, JObject>> loaded = new();
        foreach (JProperty subtype in JObject.Parse(raw).Properties())
        {
            Dictionary<string, JObject> models = [];
            foreach (JProperty model in ((JObject)subtype.Value).Properties())
            {
                models[model.Name] = WithModelDefaults((JObject)model.Value, model.Name);
            }
            loaded[subtype.Name] = models;
        }
        return loaded;
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
            ConcurrentDictionary<string, Dictionary<string, JObject>> loaded = ParseModelCache(raw);
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
        WorkerSlot slot = await GetDiscoverySlotAsync();
        ConcurrentDictionary<string, Dictionary<string, JObject>> listing = new();
        foreach (string subtype in Program.T2IModelSets.Keys)
        {
            JObject response;
            try
            {
                response = await CallWorkerAPI(slot, "ListModels", new JObject
                {
                    ["path"] = "",
                    ["depth"] = 999,
                    ["subtype"] = subtype,
                    ["allowRemote"] = false,
                    ["dataImages"] = false
                });
            }
            catch (SwarmReadableErrorException ex) when (ex.Message.Contains("Invalid sub-type"))
            {
                // A model type only a local extension adds (audio, LLM, ...): the worker has none of those.
                Logs.Debug($"[{CloudProviderName}] Worker {slot.Worker.WorkerId} has no '{subtype}' model type; skipping it.");
                continue;
            }
            Dictionary<string, JObject> models = [];
            foreach (JToken file in response["files"] as JArray ?? [])
            {
                JObject meta = file is JObject obj ? (JObject)obj.DeepClone() : new JObject { ["name"] = file.ToString() };
                string name = meta["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                models[name] = WithModelDefaults(meta, name);
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

    /// <summary>
    /// A worker to read the model list from: a running one if any, else a new lease if <see cref="MaxWorkers"/>
    /// allows, else the one already starting. Discoveries run one at a time, so concurrent ones share a worker.
    /// </summary>
    async Task<WorkerSlot> GetDiscoverySlotAsync()
    {
        await DiscoveryLock.WaitAsync(Program.GlobalProgramCancel);
        try
        {
            LeaseRequest request = MakeLeaseRequest();
            DateTime deadline = DateTime.UtcNow.AddSeconds(request.StartupTimeoutSec + 60);
            while (DateTime.UtcNow < deadline)
            {
                WorkerSlot existing = Slots.FirstOrDefault();
                if (existing is not null)
                {
                    return existing;
                }
                if (TryReserveLease())
                {
                    return await AcquireSlotAsync(CancellationToken.None);
                }
                if (ShuttingDown)
                {
                    throw new SwarmReadableErrorException("This cloud backend is shutting down.");
                }
                // At capacity with a worker already starting for a generation: use that one when it is up.
                await Task.Delay(request.PollIntervalMs, Program.GlobalProgramCancel);
            }
            throw new SwarmReadableErrorException($"{CloudProviderName} had no worker for model discovery within {request.StartupTimeoutSec}s.");
        }
        finally
        {
            DiscoveryLock.Release();
        }
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
