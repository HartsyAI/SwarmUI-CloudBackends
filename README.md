# SwarmUI-CloudBackends

A modular [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) extension that runs your generations on cloud GPUs, waking workers on demand and scaling them to zero when idle. One shared core, thin per-provider adapters — adding a new cloud provider means implementing a small interface, not rebuilding a backend.

Supersedes the older single-provider extensions `SwarmUI-Runpod-Serverless-Backend` and `SwarmUI-Vast.AI-Serverless-Backend` (do not install those alongside this — they collide on backend types, permissions, and API keys).

## Provider status

| Provider | Backend type | Status |
|---|---|---|
| RunPod Serverless | `runpod_serverless` | ✅ Supported — live-tested end to end |
| RunPod Pods | `runpod_pods` | ⚠️ Experimental, untested. Resumes an existing pod only (`AutoCreate` is not implemented) |
| Vast.ai Serverless | `vastai_serverless` | ⚠️ Experimental, untested. Requires a Vast worker image built around `workers/vastai/vast_handler.py` |

## Architecture

```
ICloudProvider            CloudBackendBase : AbstractT2IBackend
  WakeupWorkerAsync   ─┐    lazily wakes the worker on first Generate,
  StartKeepaliveAsync  ├──▶ then talks directly to the remote SwarmUI's
  StopKeepaliveAsync  ─┘    own API (GetNewSession / ListModels /
  ValidateAsync             GenerateText2Image / GenerateText2ImageWS)

CloudWorkerInfo { PublicUrl, SessionId, WorkerId, Version }
```

The remote worker runs a **full SwarmUI instance** (which manages its own ComfyUI). Once a worker is awake, every provider looks identical: a remote SwarmUI reachable at `PublicUrl`. Providers only know how to wake a GPU and keep it alive.

Integration with SwarmUI built-ins:
- Backend types registered normally — configure under **Server → Backends** (enable *Show Advanced*).
- API keys stored per-user under **User Settings → API Keys** (`runpod_api`, `vastai_api`).
- Remote models merged into the model browser via `ExtraModelProviders`; generating with a cloud-only model auto-routes to the cloud backend.
- Permissions: `use_runpod_serverless`, `use_runpod_pods`, `use_vastai`.

## RunPod Serverless setup

1. Deploy the worker: [RunPod-Worker-SwarmUI](https://github.com/HartsyAI/RunPod-Worker-SwarmUI) (Docker Hub `kalebbroo/swarmui-runpod:latest`). Endpoint requirements:
   - Network volume mounted at `/runpod-volume` (SwarmUI + models install there on first boot)
   - GPU ≥ 16 GB VRAM, container disk ~15 GB
   - Idle timeout ~120 s, **execution timeout ≥ 3600 s** (must exceed the longest keepalive the extension submits), FlashBoot recommended
2. In SwarmUI: **User Settings → API Keys** → set your RunPod key.
3. **Server → Backends** → *Show Advanced* → add **RunPod Serverless**, set `EndpointId`.
4. Enable the backend — init validates your key and endpoint via RunPod's `/health` before going live.

### Notes & limitations

- **Cloud models must be discovered before you can generate with them.** With `AutoRefresh` off, call `/API/CloudRefreshModels` (or enable `AutoRefresh`) once per server start; until then the model names are unknown to SwarmUI and generate requests are rejected with "are you sure that model name is correct?".
- The API key is read once when the backend initializes. After changing your key, disable/re-enable the backend.
- `AutoRefresh: true` wakes a paid GPU worker on every SwarmUI start to list models — leave it off unless you want that.
- `GenerationTimeoutSec` above 600 is capped by the shared HTTP client's 10-minute ceiling.
- **Never cancel a running keepalive job to "extend" it.** RunPod terminates the worker that is executing a cancelled job, so cancel-then-resubmit kills the live worker (observed: worker died ~13 s after such a cancel, and its proxy URL returned empty bodies from then on). Keepalives are blocking and run one at a time, so extending works by submitting an *additional* job that queues behind the current one. Outstanding jobs are cancelled only on shutdown, where ending the worker is the goal.

### Measured behavior (live, RTX-class worker, SDXL 1024×1024 / 12 steps)

| Operation | Time |
|---|---|
| Wake onto a FlashBoot-warm worker + model discovery (27 models) | ~15–40 s |
| Fully cold worker (container start → Swarm serving → first image) | ~3–4 min |
| First generation on an already-woken worker | ~64 s |
| Warm generation (worker reused, model resident) | ~9 s |
| Recovery from an invalidated remote session | ~9 s (session refreshed in place, no re-wake) |
| Teardown to zero running workers / zero queued jobs | immediate on backend disable |

A cold worker's proxy answers with **empty bodies** for the first few minutes — byte-identical to a dead worker. The backend retries, then waits it out rather than concluding the worker died, so a cold start costs one wake rather than a cascade of them.

## Development

Build: `dotnet build src/Extensions/SwarmUI-CloudBackends/SwarmUI-CloudBackends.csproj -c Release` (from a SwarmUI checkout with `src/bin/live_release/SwarmUI.dll` present), or just launch SwarmUI — extensions build at startup. When iterating in Release mode, SwarmUI caches the built extension DLL keyed to this repo's git HEAD; use the dev launch script or delete `src/bin/extensions/SwarmExtensionSwarmUI-CloudBackends/` after edits.

Deferred follow-ups:
- Worker handler: revalidate its cached SwarmUI session instead of returning it blindly (the extension compensates by refreshing the remote session in place on `invalid_session_id`).
- Worker repo docs (`WORKFLOW.md`/`CLIENT.md`) still describe the old blocking-wakeup contract.
- RunPod Pods `AutoCreate` (pod provisioning) and the Vast.ai path end-to-end.

## License

MIT
