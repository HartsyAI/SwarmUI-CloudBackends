# SwarmUI-CloudBackends

Run your [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) generations on cloud GPUs. Workers wake on demand when you generate and scale back to zero when idle, so you only pay while you are actually using a GPU.

The extension is modular by design: one shared core handles the SwarmUI side, and each cloud provider is a thin adapter. Adding a provider means implementing a small interface, not writing another backend.

This supersedes the older single provider extensions `SwarmUI-Runpod-Serverless-Backend` and `SwarmUI-Vast.AI-Serverless-Backend`. Do not install those alongside this one, as they collide on backend type IDs, permissions, API keys, and API route names.

## Provider status

| Provider | Backend type | Status |
|---|---|---|
| RunPod Serverless | `runpod_serverless` | Supported. Verified end to end against real hardware. |
| RunPod Pods | `runpod_pods` | Experimental, untested. Resumes an existing pod only; `AutoCreate` is not implemented. |
| Vast.ai Serverless | `vastai_serverless` | Experimental, untested. Needs a worker image built around `workers/vastai/vast_handler.py`. |

## How it works

```
  ICloudProvider                 CloudBackendBase : AbstractT2IBackend
    WakeupWorkerAsync    ---->     Wakes a GPU on the first generate, then talks
    StartKeepaliveAsync            directly to the remote SwarmUI's own HTTP API
    StopKeepaliveAsync             (GetNewSession, ListModels, SelectModel,
    ValidateAsync                  GenerateText2Image, GenerateText2ImageWS).

  CloudWorkerInfo { PublicUrl, SessionId, WorkerId, Version }
```

The remote worker runs a **full SwarmUI instance**, which manages its own ComfyUI. Once a worker is awake every provider looks the same: a remote SwarmUI reachable at `PublicUrl`. Providers only need to know how to wake a GPU and keep it alive.

This design deliberately does not reuse SwarmUI's built in `SwarmSwarmBackend`, which assumes the remote is reachable at startup and whose idle pings would keep a pay per second worker billing.

The extension plugs into SwarmUI's own systems rather than reinventing them:

* Backend types register normally, so they are configured under **Server > Backends** with *Show Advanced* enabled.
* API keys live in the per user key store under **User Settings > API Keys** (`runpod_api`, `vastai_api`).
* Remote models are merged into the model browser through `ExtraModelProviders`, and generating with a cloud only model auto routes to the cloud backend.
* Permissions: `use_runpod_serverless`, `use_runpod_pods`, `use_vastai`, and `cloudbackends_status`.

## Setup for RunPod Serverless

1. Deploy the worker: [RunPod-Worker-SwarmUI](https://github.com/HartsyAI/RunPod-Worker-SwarmUI), published as `kalebbroo/swarmui-runpod:latest`. The endpoint needs:
   * A network volume mounted at `/runpod-volume`, where SwarmUI and your models are installed on first boot.
   * A GPU with at least 16 GB VRAM, and roughly 15 GB of container disk.
   * An execution timeout comfortably above your keepalive duration (3600s is a safe default), and FlashBoot enabled.
2. In SwarmUI, open **User Settings > API Keys** and set your RunPod key.
3. Open **Server > Backends**, enable *Show Advanced*, add a **RunPod Serverless** backend, and set `EndpointId`.
4. Enable the backend. Initialization validates your key and endpoint against RunPod's health API before reporting as running, so a bad key or endpoint ID fails immediately with a clear message instead of at your first generation.

## Concurrency and scaling

One backend instance owns exactly one worker. Concurrent generation requests share it: the first request wakes the worker, and the rest reuse the cached one. `MaxConcurrent` controls how many requests SwarmUI will hand to that single backend at once, and the worker's own SwarmUI queues them internally.

Generation traffic goes straight to the worker's URL and never enters RunPod's job queue, so RunPod's autoscaler cannot see that load and will not add workers for it. To use more than one GPU, add more cloud backend instances under **Server > Backends**. Each one wakes and owns its own worker.

## Notes and limitations

* **Cloud models must be discovered before you can generate with them.** With `AutoRefresh` off, call `/API/CloudRefreshModels` once per server start, or turn `AutoRefresh` on. Until then SwarmUI does not know those model names and will reject the request.
* `AutoRefresh: true` wakes a paid GPU worker on every SwarmUI start in order to list models. Leave it off unless you want that.
* The API key is read once when the backend initializes. After changing your key, disable and re-enable the backend.
* `GenerationTimeoutSec` above 600 is capped by the shared HTTP client's 10 minute ceiling.
* `KeepaliveSeconds` (default 420) is what you pay for while idle, so lower is cheaper. It is automatically raised to at least `GenerationTimeoutSec` plus two minutes so a worker is never torn down mid generation.

### Two behaviors worth knowing about RunPod

**Cancelling a running job terminates the worker executing it.** So "cancel the old keepalive, submit a new one" kills the very worker you are trying to keep alive. Observed live: a healthy worker died about 13 seconds after such a cancel, and every later call to its proxy URL returned an empty body. Keepalives are blocking and run one at a time, so this extension extends a worker by submitting an *additional* job that queues behind the current one. Outstanding jobs are cancelled only on shutdown, where ending the worker is the point.

**A cold worker's proxy answers with empty bodies** for the first few minutes, which is byte for byte identical to a dead worker. The backend retries, then waits it out rather than concluding the worker died, so a cold start costs one wake instead of a cascade of them.

## Measured behavior

Timings from live runs against an RTX class worker, SDXL at 1024x1024 and 12 steps.

| Operation | Time |
|---|---|
| Wake onto a FlashBoot warm worker, including discovery of 27 models | 15 to 40 s |
| Fully cold worker, from container start to first image | 3 to 4 min |
| First generation on an already woken worker | about 64 s |
| Warm generation, worker reused and model resident | about 9 s |
| Recovery from an invalidated remote session | about 9 s, refreshed in place with no re-wake |
| Teardown to zero running workers and zero queued jobs | immediate on backend disable |

## Troubleshooting

Errors are written to point at the actual problem rather than leaving you guessing:

| What you see | What it means |
|---|---|
| `RunPod API key was rejected (401)` | The key in User Settings is wrong or revoked. |
| `RunPod endpoint '...' not found (404)` | The `EndpointId` backend setting does not match a real endpoint. |
| `Cloud worker '...' is running SwarmUI, but that SwarmUI has no backends configured` | The worker booted, but its own SwarmUI has no backend. Open the worker URL given in the message, go to Server > Backends, and add one. |
| `Cloud worker '...' refused to load model '...'` | The model is not present on the worker, or its backend cannot load it. |
| `did not complete within Ns` | The worker did not wake in time. Raise `StartupTimeoutSec`, or check the RunPod console for capacity problems. |

For anything else, run SwarmUI with `--loglevel verbose` and look for lines tagged `[RunPod Serverless]` or `[CloudBackends]`.

## Development

Build with `dotnet build src/Extensions/SwarmUI-CloudBackends/SwarmUI-CloudBackends.csproj -c Release` from a SwarmUI checkout, or just launch SwarmUI, which builds extensions at startup.

One gotcha while iterating: SwarmUI caches the built extension DLL against this repository's git HEAD, so in Release mode it skips rebuilds until there is a new commit here. Either use the dev launch script or delete `src/bin/extensions/SwarmExtensionSwarmUI-CloudBackends/` after editing.

Known follow ups:

* The worker handler returns its cached SwarmUI session without revalidating it. The extension compensates by refreshing the remote session in place when it sees `invalid_session_id`.
* RunPod Pods provisioning (`AutoCreate`) and the Vast.ai path end to end.
* All requests through one backend share a single remote session, so an interrupt cancels every in flight generation on that worker. Per request remote sessions would fix it.

## License

MIT
