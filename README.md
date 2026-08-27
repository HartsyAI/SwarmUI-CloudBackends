# SwarmUI-CloudBackends

Run your [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) generations on cloud GPUs. Workers wake on demand when you generate and scale back to zero when idle, so you only pay while you are actually using a GPU.

The extension is modular by design: one shared core handles the SwarmUI side, and each cloud provider is a thin adapter. Adding a provider means implementing a small interface, not writing another backend.

This supersedes the older single provider extensions `SwarmUI-Runpod-Serverless-Backend` and `SwarmUI-Vast.AI-Serverless-Backend`. Do not install those alongside this one, as they collide on backend type IDs, permissions, API keys, and API route names.

## Provider status

| Provider | Backend type | Status |
|---|---|---|
| RunPod Serverless | `runpod_serverless` | Supported. Verified end to end against real hardware, including generation. |
| RunPod Pods | `runpod_pods` | Built on REST API v2. Pod lifecycle verified live: create, start, status, stop, terminate. Generation end to end still needs a pod image that serves SwarmUI on the exposed http port (see below). |
| Vast.ai Serverless | `vastai_serverless` | Built to the documented serverless contract. Config and credential handling verified against the live API; routing and generation are untested, since that needs a Vast account and a deployed worker. |

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

## Setup for RunPod Pods

A pod is a GPU you rent by the hour, so unlike serverless it bills continuously from the moment it starts until you stop it. The backend stops the pod when you disable it, which is on by default; leave it that way unless you have a reason not to.

Point the backend at an existing pod by setting `PodId`, or turn on `AutoCreate` and give it an `ImageName` (or `TemplateId`). With AutoCreate the backend looks for a pod matching `PodName` before creating one, so restarting SwarmUI reuses your pod instead of leaving another one running.

The pod's image must serve SwarmUI on `SwarmUIPort`, and that port is exposed as an http port so RunPod's proxy can reach it at `https://{podId}-{port}.proxy.runpod.net`. Note that RunPod's proxy applies no authentication of its own: anyone who knows the pod ID and port can reach that SwarmUI, so do not put anything sensitive on a pod you would not expose publicly.

If you attach a `NetworkVolumeId`, the pod is automatically placed in that volume's data center, because a volume can only attach to a pod sitting next to it.

Leaving `GpuTypeId` blank makes the backend ask RunPod's catalog which GPUs are actually available for pods and try them cheapest first. This matters because the v2 API places exactly one GPU type per request and will not fall back on its own: naming a single busy GPU type just fails with a capacity error.

> A worker image built for RunPod **serverless** will not generally work as a pod. The serverless image's entrypoint runs the serverless job handler, which exits outside that environment, so nothing ends up listening on the http port and the proxy answers 404. A pod image needs to start SwarmUI and keep it running in the foreground.

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

### Which APIs this targets

* **RunPod Pods** uses REST API **v2** (`https://api.runpod.io/v2`). RunPod retires REST v1 on 2026-11-15 and GraphQL in early 2027, so neither is a safe target. v2 is not a rename of v1: status is a single observed value across six states rather than v1's three-state `desiredStatus`, state changes go through one `/action` endpoint, and several field names differ.
* **RunPod Serverless** uses the job API (`/v2/{endpointId}/run`, `/status`, `/cancel`, `/health`), which is a separate surface and unaffected by the v1 retirement.
* **Vast.ai** uses `POST https://run.vast.ai/route/` for routing plus the console REST API at `https://console.vast.ai` for endpoint lookup. The grant returned by `/route/` is forwarded to the worker verbatim as `auth_data`, because its signature covers those exact fields.

Known follow ups:

* The worker handler returns its cached SwarmUI session without revalidating it. The extension compensates by refreshing the remote session in place when it sees `invalid_session_id`.
* A pod image that runs SwarmUI in the foreground, so the Pods path can be proven end to end rather than only through its lifecycle.
* The Vast.ai path end to end, which needs an account, a workergroup and a deployed worker.
* All requests through one backend share a single remote session, so an interrupt cancels every in flight generation on that worker. Per request remote sessions would fix it.
* Vast.ai's `/route/` signature is verified inside Vast's own SDK and the algorithm is not published, so `workers/vastai/vast_handler.py` does not verify it. Treat that handler as trusted-network only until it does.

## License

MIT
