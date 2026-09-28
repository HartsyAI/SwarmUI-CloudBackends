# Cloud Backends: architecture

This is for contributors. The user guide is the [README](../README.md).

## Shape

One backend type is public: **Cloud Backends** (`CloudBackendsBackend`). It never generates. For each user who uses it, it creates hidden per-user children, one per enabled provider section, each running on that user's own API key:

| Child | Base class | Provider interface |
|---|---|---|
| RunPod Serverless | `CloudBackendBase` | `ICloudProvider` (`RunPodServerlessProvider`) |
| Vast.ai Serverless | `CloudBackendBase` | `ICloudProvider` (`VastAIProvider`) |
| RunPod GPU Pods | `CloudInstanceBackendBase` | `ICloudInstanceProvider` (`RunPodPodsProvider`) |
| Vast.ai Instances | `CloudInstanceBackendBase` | `ICloudInstanceProvider` (`VastAIInstanceProvider`) |

The child types are built outside the public registry (`CloudBackendTypes`), so they cannot be added on their own. Serverless children are created by a `PreGenerateEvent` hook (the only place before backend matching that knows the user); instance children only by the explicit start and status routes. **Nothing created implicitly ever spends money.**

In both shapes, generation is core SwarmUI code: the child attaches an `OwnerBoundSwarmBackend` (core's `SwarmSwarmBackend` plus an ownership gate) to the worker's SwarmUI. That child mirrors the worker's own backends as grandchildren, and core routes generations straight to them. Sessions, previews, model loading and WebSocket generation all come from core.

## Workers and their images

Workers run Hartsy's worker images, built on [SwarmUI-Worker-Base](https://github.com/HartsyAI/SwarmUI-Worker-Base):

- SwarmUI listens on loopback only. A gateway on the public port requires `Authorization: Bearer <token>` on HTTP and WebSocket. Core's swarm backend sends its `AuthorizationHeader` setting on both (`SwarmSwarmBackend.RequestAdapter`, and the WebSocket connect), and grandchildren inherit their parent's settings, so setting it once on the attached child covers the whole tree.
- Idle is measured on the worker from SwarmUI's own `/API/GetGlobalStatus` counters (generations and model loads, never HTTP traffic, because the attached swarm backend polls constantly).
- Models on a shared volume are read through a per-worker symlink tree, so SwarmUI's per-folder `model_metadata.ldb` is never written by several workers at once.

## Serverless: leases

`ICloudProvider` is built around one idea: **each worker is held by exactly one provider-native lease.**

| | RunPod | Vast.ai |
|---|---|---|
| Lease | A `lease` job on a queue endpoint, handled by an async-generator handler. Its first streamed output (`/stream/<job>`) is `{public_url, token, protocol: 2, ...}`; the job keeps running while the worker is in use. | A native PyWorker **session** (`/route/`, then `/session/create` on the worker, cost 100, `max_sessions = 1`), then the worker's `/lease` route for `{public_url, token}`. |
| Who ends it | The worker, after `IdleSeconds` with no activity (or at `MaxLeaseSeconds`). | The session's TTL, unless renewed while in use. |
| Renewal | None needed. | Each request made with the session adds a whole lifetime (`expiration += lifetime`), so `RenewLeaseAsync` renews only when less than half a lifetime (+15 s) remains. |
| Scale-out signal | A running lease occupies its worker, so a queued lease is real queue pressure for RunPod's scaler. | An open session counts as a full worker's load (the worker's benchmark is calibrated so cost 100 equals its capacity), so the next session routes to, or recruits, another worker. |
| Explicit release | Cancel the job (RunPod stops the worker running it). | `/session/end`. |
| SwarmUI crashes | Workers still idle out. | Sessions expire within one lifetime. |
| TLS | RunPod's HTTPS proxy. | Vast's instance certificate, pinned to Vast's root CA (`VastTls`, embedded and fingerprint-checked). Core's swarm backend only trusts the system store, so it connects through a loopback relay (`VastTlsRelay`). |

RunPod's load-balancing endpoints were considered and rejected: no session affinity (SwarmUI state is per worker), a 5.5-minute per-request limit, and unclear WebSocket scaling. Details, with sources, are in the worker repos' `docs/PROVIDER_NOTES.md`.

### `CloudBackendBase`

- `Slots` holds one `WorkerSlot` per leased worker: its `CloudWorkerInfo`, the attached child, and the connect URL (the worker URL, or the TLS relay).
- **Routing** (`IsValidForThisBackend`). The cloud backend accepts a request only when:
  1. it belongs to the requester;
  2. no free backend outside a serverless subtree can serve it (local GPUs and already-rented instances first; core may otherwise pick the cloud backend to load a model on);
  3. no free worker backend can serve it;
  4. `ShouldTakeRequest`: there is room to lease another worker, or no worker is up yet (so the request queues here rather than failing).
- **Explicitly targeted requests.** Core matches a requested backend type exactly (`T2IEngine`), and the mirrored worker backends are a different type, so a request that targets the serverless type can only reach the cloud backend. It then always accepts and hands the request over itself. The extension never rewrites a request's backend type, because that would shut the mirrored backends out of warm generations.
- **Handoff** (`ClaimGeneratorAsync`). Claims a free worker backend through `T2IBackendAccess` (under `SlotLock`, so two handoffs never take the same one). Otherwise it starts a lease, and keeps checking existing workers while it starts; if one frees up first, the new lease is withdrawn if the provider has not assigned a worker yet (a RunPod job still `IN_QUEUE`), or kept as a slot if it has.
- `MaxUsages` is well above `MaxWorkers`, because a request holds its usage for the whole handed-off generation. Leasing is limited separately, by slots plus leases starting, so extra usages only let requests queue here while workers start.
- **Maintenance** (`OnTick`, every 5 s): renews in-use leases (throttled per slot) and checks each lease every 15 s; a lease that has ended is removed (model list kept, child detached, lease released).
- **Models**: `LoadModel` never leases (the worker loads the model as part of the generation). The model list is adopted from a slot's child when it is removed (each reported model type's list replaces the old one), and saved per user and endpoint with `SaveGenericData` (names plus a few small fields, never previews). `RemoteModels` feeds the model browser via `ExtraModelProviders`.

## Instances

`CloudInstanceBackendBase` drives the provider API to start, stop or create an instance, waits for its SwarmUI to answer (authenticated), and attaches the child. The instance's token is generated once per user and card section, saved with the remembered instance ID, and injected as `SWARMUI_WORKER_TOKEN` when the card creates the instance. Failsafes stop an instance after a runtime or spend cap. Orphans (running instances named `swarmui-cloudbackends...` that no card has attached) are listed and stopped only through explicit routes.

## Provider APIs

- **RunPod Serverless**: job API at `api.runpod.ai/v2/<endpoint>/` (`run`, `stream`, `status`, `cancel`, `health`); endpoint configuration from `rest.runpod.io/v1/endpoints/<id>`. 429 and 5xx back off with `Retry-After` (`HttpRetry`). An endpoint on the version 1 image answers the lease with its "Unknown action" refusal; the provider then holds workers the 1.x way (a `wakeup` job, then `keepalive` jobs topped up at half a window while in use, cancelled on release), with `WorkerLimit()` 1 and no gateway token.
- **RunPod Pods**: REST API v2 (`api.runpod.io/v2`). Pods have no labels, so orphans are recognised by name prefix.
- **Vast.ai Serverless**: `run.vast.ai/route/` for grants, the worker's PyWorker for sessions (exact wire format of `vastai/serverless/client/client.py`), and `console.vast.ai/api/v0/endptjobs/` for endpoint settings.
- **Vast.ai Instances**: `console.vast.ai/api/v0` and `/api/v1/instances/`.

## Testing

- `Tests/`: NUnit tests for the pure logic (routing rule, model cache, lease parsing, renewal timing, TLS pinning, backoff).
- The worker repos have their own unit tests and CPU smoke tests in CI.
- Live provider behavior is verified by running SwarmUI against real endpoints.
