# SwarmUI API Documentation - CloudBackendsWebAPI

> This is a subset of the API docs, see [/docs/API.md](/docs/API.md) for general info.

API routes for managing cloud GPU backends (RunPod, Vast.ai) added by the Cloud Backends extension.

#### Table of Contents:

- HTTP Route [CloudClearModelCache](#http-route-apicloudclearmodelcache)
- HTTP Route [CloudGetPodStatus](#http-route-apicloudgetpodstatus)
- HTTP Route [CloudGetStatus](#http-route-apicloudgetstatus)
- HTTP Route [CloudListEndpoints](#http-route-apicloudlistendpoints)
- HTTP Route [CloudListOrphans](#http-route-apicloudlistorphans)
- HTTP Route [CloudListProviders](#http-route-apicloudlistproviders)
- HTTP Route [CloudListRunPodOptions](#http-route-apicloudlistrunpodoptions)
- HTTP Route [CloudListWorkers](#http-route-apicloudlistworkers)
- HTTP Route [CloudRefreshModels](#http-route-apicloudrefreshmodels)
- HTTP Route [CloudStartPod](#http-route-apicloudstartpod)
- WebSocket Route [CloudStartPodWS](#websocket-route-apicloudstartpodws)
- HTTP Route [CloudStopOrphan](#http-route-apicloudstoporphan)
- HTTP Route [CloudStopPod](#http-route-apicloudstoppod)
- HTTP Route [CloudStopWorker](#http-route-apicloudstopworker)
- HTTP Route [CloudValidateBackend](#http-route-apicloudvalidatebackend)
- HTTP Route [VastAIGetInstanceStatus](#http-route-apivastaigetinstancestatus)
- HTTP Route [VastAIListInstanceOptions](#http-route-apivastailistinstanceoptions)
- HTTP Route [VastAIStartInstance](#http-route-apivastaistartinstance)
- WebSocket Route [VastAIStartInstanceWS](#websocket-route-apivastaistartinstancews)
- HTTP Route [VastAIStopInstance](#http-route-apivastaistopinstance)

## HTTP Route /API/CloudClearModelCache

#### Description

Forgets the remembered model list of the user's serverless endpoints, so it is learned again from a worker (the next generation with a model you have locally, or Discover models).

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' card (or of one of its serverless backends). | **(REQUIRED)** |
| provider | String | Optional settings prefix to limit this to one provider: 'RunPodServerless_' or 'VastAI_'. Empty for all. | (Empty String) |

#### Return Format

```js
    "success": true,
    "cleared": 1
```

## HTTP Route /API/CloudGetPodStatus

#### Description

Live status of the pod behind a RunPod GPU Pods backend - state, GPU, cost/hr, uptime, and which actions RunPod currently allows on it. Cached briefly server-side so a UI poll does not hammer RunPod's API.

#### Permission Flag

`use_runpod_pods` - `Use RunPod GPU Pods` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to query. | **(REQUIRED)** |
| force_refresh | Boolean | If true, bypass the short server-side status cache (e.g. right after a Start/Stop). | `False` |

#### Return Format

```js
    "has_instance": true, // false with a "message" when no pod is resolved yet
    "instance_id": "abc123",
    "status": "RUNNING",
    "gpu_name": "NVIDIA GeForce RTX 4090",
    "gpu_count": 1,
    "cost_per_hour": 0.44,
    "uptime_seconds": 123,
    "allowed_actions": ["stop", "restart"],
    "public_url": "https://abc123-7801.proxy.runpod.net"
```

## HTTP Route /API/CloudGetStatus

#### Description

Gets status for every running cloud backend the user may access, across all providers - serverless workers and rented instances alike.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

**None.**

#### Return Format

```js
    "backends":
    [
        {
            "id": 1,
            "title": "titlehere",
            "provider": "RunPod Serverless",
            "kind": "serverless", // or "instance"
            "status": "RUNNING",
            // serverless: "endpoint", "model_count", "workers", "workers_starting", "max_workers"
            // instance: "instance_id", "instance_url", "child_backend_id"
        }
    ],
    "total": 1
```

## HTTP Route /API/CloudListEndpoints

#### Description

Lists the user's serverless endpoints on one provider, for the card's endpoint dropdown. 'id' is the value the EndpointId setting takes (the endpoint ID on RunPod, the endpoint name on Vast.ai).

#### Permission Flag

`view_backends_list` - `View Backends List` in group `Backends Admin`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| provider | String | The serverless provider's settings prefix: 'RunPodServerless_' or 'VastAI_'. | **(REQUIRED)** |

#### Return Format

```js
    "endpoints": [{"id": "...", "name": "...", "image": "...", "max_workers": 3}]
```

## HTTP Route /API/CloudListOrphans

#### Description

Lists running pods and instances on the user's own RunPod and Vast.ai accounts that this extension created but that no Cloud Backends card has attached right now - for example after SwarmUI stopped uncleanly, or a section was disabled while its instance ran. They bill until stopped. Stopping is only ever done explicitly, with CloudStopOrphan.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

**None.**

#### Return Format

```js
    "orphans":
    [
        {"provider": "runpod", "id": "podid", "name": "swarmui-cloudbackends-3", "status": "RUNNING", "cost_per_hr": 0.44}
    ],
    "errors": [{"provider": "vastai", "error": "..."}] // providers that could not be checked
```

## HTTP Route /API/CloudListProviders

#### Description

Lists the cloud providers this extension supports, with their settings prefixes, setup guide links, which settings belong in the card's Advanced group, and (for instance-renting providers) their start/stop/status route names - so a UI can build its provider table from the server instead of hardcoding it.

#### Permission Flag

`view_backends_list` - `View Backends List` in group `Backends Admin`

#### Parameters

**None.**

#### Return Format

```js
    "providers":
    [
        {
            "prefix": "RunPodPods_",
            "label": "RunPod GPU Pods",
            "key_type": "runpod_api",
            "is_instance": true,
            "guide_url": "https://github.com/HartsyAI/SwarmUI-CloudBackends/blob/main/docs/setup/runpod-pods.md",
            "advanced_fields": ["RunPodPods_TemplateId", "..."],
            "images": [{"image": "kalebbroo/swarmui-worker-runpod:2.0.0-comfyui", "label": "Hartsy worker, ComfyUI"}], // only for instance providers
            "item_label": "Pod", // only for instance providers
            "start_call": "CloudStartPod",
            "start_call_ws": "CloudStartPodWS",
            "stop_call": "CloudStopPod",
            "status_call": "CloudGetPodStatus"
        }
    ]
```

## HTTP Route /API/CloudListRunPodOptions

#### Description

Lists what the user's RunPod account can actually use right now: GPU types with live availability and price, network volumes, data centers, templates, and pods. Exists so the backend settings form can offer real choices instead of asking someone to type an exact GPU name and hope it is spelled right and in stock.

#### Permission Flag

`use_runpod_pods` - `Use RunPod GPU Pods` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| cloud_type | String | Which RunPod cloud to list for: 'SECURE' (default) or 'COMMUNITY'. | `SECURE` |

#### Return Format

```js
    "cloud": "SECURE",
    "gpus": [{"id": "...", "name": "...", "memory_gb": 24, "price_per_hr": 0.44, "max_count": 8, "availability": "HIGH", "data_centers": ["..."]}],
    "network_volumes": [{"id": "...", "name": "...", "size_gb": 100, "data_center": "..."}],
    "templates": [{"id": "...", "name": "...", "image": "..."}], // pod templates only
    "data_centers": [{"id": "...", "name": "..."}],
    "pods": [{"id": "...", "name": "...", "status": "...", "gpu": "...", "cost_per_hr": 0.44}]
```

## HTTP Route /API/CloudListWorkers

#### Description

Lists the requesting user's leased serverless workers under a Cloud Backends card: one entry per provider, each with its workers and their backends. Never includes access tokens.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' card (or of one of its serverless backends). | **(REQUIRED)** |
| provider | String | Optional settings prefix to limit the list to one provider: 'RunPodServerless_' or 'VastAI_'. Empty for all. | (Empty String) |

#### Return Format

```js
    "backends":
    [
        {
            "backend_id": -12, // the user's hidden serverless backend
            "provider": "RunPod Serverless",
            "endpoint": "abc123",
            "max_workers": 2,
            "workers_starting": 0,
            "workers":
            [
                {
                    "worker_id": "podid",
                    "lease_id": "job-or-session-id",
                    "public_url": "https://podid-7801.proxy.runpod.net",
                    "age_seconds": 95,
                    "in_use": true,
                    "child_backend_id": -13,
                    "backends": [{"id": -14, "status": "running", "usages": 1, "max_usages": 2}]
                }
            ]
        }
    ]
```

## HTTP Route /API/CloudRefreshModels

#### Description

Discovers the models of the user's serverless endpoints: uses a running worker, or leases one (billed until it goes idle). Scope it to one card and provider with backend_id and provider; with neither, every serverless endpoint of the user is refreshed, each leasing its own worker. Rented-instance backends mirror models through core's own Swarm backend instead.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' card to discover for. 0 for every card. | `0` |
| provider | String | Settings prefix of the one provider to discover for: 'RunPodServerless_' or 'VastAI_'. Empty for every serverless provider. | (Empty String) |

#### Return Format

```js
    "refreshed": 2, // backends successfully refreshed
    "failed": 0, // backends that errored
    "errors": [{"backend_id": 1, "error": "reason"}],
    "message": "Refreshed 2 cloud backend(s), 0 failed."
```

## HTTP Route /API/CloudStartPod

#### Description

Starts (or creates, per settings) the cloud pod behind a RunPod GPU Pods backend and attaches its Swarm backend. Blocks until the pod's SwarmUI is reachable, which can take minutes on a cold start.

#### Permission Flag

`use_runpod_pods` - `Use RunPod GPU Pods` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "message": "Instance started for backend #1."
```

## WebSocket Route /API/CloudStartPodWS

#### Description

Websocket variant of CloudStartPod: streams the backend's status lines as {"status": ...} while the pod starts (minutes on a cold start), then a final {"message": ..., "done": true} or {"error": ...}.

#### Permission Flag

`use_runpod_pods` - `Use RunPod GPU Pods` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "status": "Starting RunPod GPU Pods instance (up to 900s)..." // repeated, then:
    "message": "Instance started for backend #1.", "done": true
```

## HTTP Route /API/CloudStopOrphan

#### Description

Stops (does not destroy) one orphaned pod or instance listed by CloudListOrphans, on the user's own account. Its disk is kept, so it can be started again.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| provider | String | 'runpod' or 'vastai'. | **(REQUIRED)** |
| resource_id | String | Pod or instance ID, as listed by CloudListOrphans. | **(REQUIRED)** |

#### Return Format

```js
    "success": true
```

## HTTP Route /API/CloudStopPod

#### Description

Stops the cloud pod behind a RunPod GPU Pods backend, detaching its Swarm backend first. The pod stays stopped until started again.

#### Permission Flag

`use_runpod_pods` - `Use RunPod GPU Pods` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose RunPod GPU Pods section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "message": "Instance stopped for backend #1."
```

## HTTP Route /API/CloudStopWorker

#### Description

Releases one leased serverless worker now: detaches its backends and ends its lease, which stops billing for it. Anything it was generating fails.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' card (or of one of its serverless backends). | **(REQUIRED)** |
| worker_id | String | Worker ID or lease ID, as listed by CloudListWorkers. | **(REQUIRED)** |

#### Return Format

```js
    "success": true
```

## HTTP Route /API/CloudValidateBackend

#### Description

Checks the provider-side configuration of the user's serverless endpoints against the card's settings (execution timeout vs lease length, endpoint max workers, image version, Vast scale-to-zero settings). Makes no changes and bills nothing.

#### Permission Flag

`cloudbackends_status` - `Cloud Backends Status/Refresh` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' card (or of one of its serverless backends). | **(REQUIRED)** |

#### Return Format

```js
    "results":
    [
        {
            "provider": "RunPod Serverless",
            "backend_id": -12,
            "findings": [{"level": "error", "message": "..."}] // empty when all is well
        }
    ]
```

## HTTP Route /API/VastAIGetInstanceStatus

#### Description

Live status of the instance behind a Vast.ai Instances backend - state, GPU, cost/hr, uptime. Cached briefly server-side so a UI poll does not hammer Vast's API.

#### Permission Flag

`use_vastai_instances` - `Use Vast.ai Instances` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose Vast.ai Instances section to query. | **(REQUIRED)** |
| force_refresh | Boolean | If true, bypass the short server-side status cache (e.g. right after a Start/Stop). | `False` |

#### Return Format

```js
    "has_instance": true, // false with a "message" when no instance is resolved yet
    "instance_id": "12345678",
    "status": "running",
    "gpu_name": "RTX 4090",
    "gpu_count": 1,
    "cost_per_hour": 0.31,
    "uptime_seconds": 123,
    "allowed_actions": [], // Vast publishes no allowed-actions list
    "public_url": "http://1.2.3.4:45678"
```

## HTTP Route /API/VastAIListInstanceOptions

#### Description

Lists what the user's Vast.ai account can use right now: the GPU models on offer (single-GPU on-demand machines) with their lowest price, and the account's own instances, templates and network volumes. Same purpose as CloudListRunPodOptions.

#### Permission Flag

`use_vastai_instances` - `Use Vast.ai Instances` in group `CloudBackends`

#### Parameters

**None.**

#### Return Format

```js
    "gpus": [{"name": "RTX 4090", "gpu_ram": 24, "min_price": 0.31, "offers": 42}],
    "network_volumes": [{"id": "...", "name": "...", "size_gb": 100}],
    "instances": [{"id": "...", "label": "...", "status": "running", "gpu": "RTX 4090", "num_gpus": 1}],
    "templates": [{"id": "<template hash>", "name": "...", "image": "..."}]
```

## HTTP Route /API/VastAIStartInstance

#### Description

Starts (or creates, per settings) the Vast.ai instance behind a Vast.ai Instances backend and attaches its Swarm backend. Blocks until the instance's SwarmUI is reachable, which can take minutes on a cold start.

#### Permission Flag

`use_vastai_instances` - `Use Vast.ai Instances` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "message": "Instance started for backend #1."
```

## WebSocket Route /API/VastAIStartInstanceWS

#### Description

Websocket variant of VastAIStartInstance: streams the backend's status lines as {"status": ...} while the instance starts (minutes on a cold start), then a final {"message": ..., "done": true} or {"error": ...}.

#### Permission Flag

`use_vastai_instances` - `Use Vast.ai Instances` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "status": "Starting Vast.ai Instances instance (up to 900s)..." // repeated, then:
    "message": "Instance started for backend #1.", "done": true
```

## HTTP Route /API/VastAIStopInstance

#### Description

Stops the Vast.ai instance behind a Vast.ai Instances backend, detaching its Swarm backend first. The instance stays stopped until started again.

#### Permission Flag

`use_vastai_instances` - `Use Vast.ai Instances` in group `CloudBackends`

#### Parameters

| Name | Type | Description | Default |
| --- | --- | --- | --- |
| backend_id | Int32 | ID of the 'Cloud Backends' backend whose Vast.ai Instances section to act on. | **(REQUIRED)** |

#### Return Format

```js
    "message": "Instance stopped for backend #1."
```

