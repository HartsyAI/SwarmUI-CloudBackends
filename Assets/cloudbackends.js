/**
 * Two jobs for the single "Cloud Backends" settings card:
 *
 * 1. Fills the RunPod GPU Pods section with live choices from the user's RunPod account: which GPUs are
 *    actually rentable right now and what they cost, which network volumes exist, which data centers,
 *    which templates, and which pods are already running.
 *
 *    Approach: rewrite the cached settings schema in `backend_types['cloud_backends']` and let Swarm's
 *    own renderer draw the dropdowns. Swarm builds the card straight from that schema, so patching the
 *    schema means every render path (add, save, and the status poll) draws the right control with the
 *    right `data-name`, and saving keeps working untouched. Core does the same thing for VAE lists.
 *
 *    If the RunPod call fails or no API key is set, the schema is left alone and the fields stay as
 *    plain text boxes, which still work if you know the exact ids.
 *
 * 2. Regroups the card's flat field list into one collapsible section per provider (RunPod Serverless /
 *    RunPod GPU Pods / Vast.ai), each with its own Enabled toggle pulled up into the section header.
 *    Purely a DOM reorganization after Swarm's normal renderer draws the flat card - every field keeps
 *    its original `data-name`, so Save/Edit/load keep working exactly as core expects. Each section also
 *    gets an empty `.cloudbackends-provider-actions` footer as a hook for provider-specific buttons later
 *    (start a pod, pick a GPU, etc) without touching this regrouping logic again.
 */

let runpodOptions = null;
let runpodOptionsPending = false;

const CLOUDBACKENDS_TYPE_ID = 'cloud_backends';

/** One entry per provider section, in the order fields are declared in CloudBackendsBackend.Settings. */
const CLOUDBACKENDS_PROVIDERS = [
    { prefix: 'RunPodServerless_', label: 'RunPod Serverless' },
    { prefix: 'RunPodPods_', label: 'RunPod GPU Pods' },
    { prefix: 'VastAI_', label: 'Vast.ai Serverless' }
];

/** Fields that become dropdowns, mapped to a builder over the fetched options. */
function runpodBuildChoices() {
    if (!runpodOptions) {
        return null;
    }
    function withBlank(items, blankLabel, valueOf, labelOf) {
        let out = { values: [''], names: [blankLabel] };
        for (let item of items || []) {
            let val = valueOf(item);
            if (!val) {
                continue;
            }
            out.values.push(val);
            out.names.push(labelOf(item));
        }
        return out;
    }
    let gpus = withBlank(runpodOptions.gpus, '(cheapest available)',
        g => g.id,
        g => {
            let bits = [g.name || g.id];
            if (g.memory_gb) {
                bits.push(`${g.memory_gb}GB`);
            }
            if (g.price_per_hr) {
                bits.push(`$${g.price_per_hr}/hr`);
            }
            if (g.availability) {
                bits.push(g.availability.toLowerCase());
            }
            return bits.join(' · ');
        });
    let volumes = withBlank(runpodOptions.network_volumes, '(no network volume)',
        v => v.id,
        v => `${v.name || v.id} · ${v.size_gb || '?'}GB${v.data_center ? ' · ' + v.data_center : ''}`);
    let dcs = withBlank(runpodOptions.data_centers, '(let RunPod choose)',
        d => d.id,
        d => d.name && d.name != d.id ? `${d.id} · ${d.name}` : d.id);
    let templates = withBlank(runpodOptions.templates, '(none, use an image)',
        t => t.id,
        t => t.image ? `${t.name || t.id} · ${t.image}` : (t.name || t.id));
    let pods = withBlank(runpodOptions.pods, '(create or reuse by name)',
        p => p.id,
        p => `${p.name || p.id} · ${p.status || '?'}${p.gpu ? ' · ' + p.gpu : ''}`);
    return {
        'RunPodPods_GpuTypeId': gpus,
        'RunPodPods_NetworkVolumeId': volumes,
        'RunPodPods_DataCenterId': dcs,
        'RunPodPods_TemplateId': templates,
        'RunPodPods_PodId': pods,
        'RunPodPods_GpuCount': { values: ['1', '2', '4', '8'], names: ['1', '2', '4', '8'] },
        'RunPodPods_CloudType': { values: ['SECURE', 'COMMUNITY'], names: ['Secure Cloud', 'Community Cloud'] }
    };
}

/** Rewrites the cached cloud_backends schema so Swarm renders live dropdowns instead of text boxes. */
function runpodApplySchema() {
    let type = typeof backend_types != 'undefined' ? backend_types[CLOUDBACKENDS_TYPE_ID] : null;
    if (!type || !type.settings) {
        return;
    }
    let choices = runpodBuildChoices();
    if (!choices) {
        return;
    }
    for (let setting of type.settings) {
        let choice = choices[setting.name];
        // Leave the field as-is when the account has nothing to offer, so it stays typeable
        // rather than becoming a dropdown whose only entry is "none".
        if (!choice || choice.values.length <= 1) {
            continue;
        }
        setting.type = 'dropdown';
        setting.values = choice.values;
        setting.value_names = choice.names;
    }
}

/** Fetches the account's options, then repaints any visible cloud_backends cards. */
function runpodRefreshOptions(then = null) {
    if (runpodOptionsPending) {
        return;
    }
    runpodOptionsPending = true;
    genericRequest('CloudListRunPodOptions', { 'cloud_type': 'SECURE' }, data => {
        runpodOptionsPending = false;
        if (data && data.success) {
            runpodOptions = data;
            runpodApplySchema();
            if (then) {
                then();
            }
        }
        else if (data && data.error) {
            console.log(`[CloudBackends] RunPod options unavailable: ${data.error}`);
        }
    }, 0, e => {
        runpodOptionsPending = false;
        console.log(`[CloudBackends] Could not load RunPod options, leaving text inputs: ${e}`);
    });
}

/**
 * Regroups a freshly-rendered cloud_backends card's flat field list into one collapsible section per
 * provider, each with its own Enabled toggle in the header. Every field div keeps its original
 * `data-name` input untouched (just moved in the DOM), so Save/Edit continue to work unmodified.
 */
function cloudBackendsReorganizeCard(cardBody, backendId) {
    let settingDivs = [...cardBody.children].filter(div => div.querySelector('[data-name]'));
    for (let provider of CLOUDBACKENDS_PROVIDERS) {
        let matches = settingDivs.filter(div => div.querySelector('[data-name]').dataset.name.startsWith(provider.prefix));
        if (matches.length == 0) {
            continue;
        }
        let toggleDiv = matches.find(div => div.querySelector('[data-name]').dataset.name == `${provider.prefix}Enabled`);
        let section = createDiv(null, 'cloudbackends-provider-section');
        let header = createDiv(null, 'cloudbackends-provider-header');
        let caret = document.createElement('span');
        caret.className = 'cloudbackends-provider-caret';
        caret.innerText = '▶';
        let title = document.createElement('span');
        title.className = 'cloudbackends-provider-title';
        title.innerText = provider.label;
        header.appendChild(caret);
        header.appendChild(title);
        let body = createDiv(null, 'cloudbackends-provider-body');
        body.style.display = 'none';
        if (toggleDiv) {
            // Mirror the Enabled checkbox into the header for at-a-glance state and one-click toggling
            // without expanding the section; the original stays in the body for its label/popover.
            let toggleInput = toggleDiv.querySelector('[data-name]');
            let headerToggle = toggleInput.cloneNode(true);
            headerToggle.removeAttribute('id');
            headerToggle.removeAttribute('data-name');
            headerToggle.checked = toggleInput.checked;
            headerToggle.addEventListener('click', e => e.stopPropagation());
            headerToggle.addEventListener('change', () => { toggleInput.checked = headerToggle.checked; });
            toggleInput.addEventListener('change', () => { headerToggle.checked = toggleInput.checked; });
            header.appendChild(headerToggle);
        }
        let actions = createDiv(null, `cloudbackends-provider-actions cloudbackends-actions-${provider.prefix.slice(0, -1).toLowerCase()}`);
        let podControls = provider.prefix == 'RunPodPods_' ? cloudBackendsSetupPodActions(actions, backendId) : null;
        let statusLoadedOnce = false;
        header.addEventListener('click', () => {
            let opening = body.style.display == 'none';
            body.style.display = opening ? 'block' : 'none';
            caret.innerText = opening ? '▼' : '▶';
            // Load status lazily on first expand, not on every render/poll, so a collapsed section never
            // spends a RunPod API call (the server-side cache in RunPodPodsProvider.GetStatusAsync covers
            // the rest: repeat expands within its TTL are free).
            if (opening && podControls && !statusLoadedOnce) {
                statusLoadedOnce = true;
                podControls.refresh();
            }
        });
        section.appendChild(header);
        section.appendChild(body);
        // Anchor the section at the first matched field's original position BEFORE moving anything -
        // once a match is re-parented into `body` it is no longer in `cardBody` to anchor against.
        matches[0].before(section);
        for (let div of matches) {
            body.appendChild(div); // re-parents out of cardBody, original data-name inputs untouched
        }
        body.appendChild(actions);
    }
}

/** Seconds -> compact "1h05m" / "5m12s" / "42s" for the pod status line. */
function cloudBackendsFormatUptime(seconds) {
    if (!seconds || seconds <= 0) {
        return '0s';
    }
    let h = Math.floor(seconds / 3600);
    let m = Math.floor((seconds % 3600) / 60);
    let s = Math.floor(seconds % 60);
    if (h > 0) {
        return `${h}h${String(m).padStart(2, '0')}m`;
    }
    if (m > 0) {
        return `${m}m${String(s).padStart(2, '0')}s`;
    }
    return `${s}s`;
}

/**
 * Best-effort price line for the Start Pod confirmation, read from the currently-selected GPU type field
 * against the already-fetched RunPod catalog (runpodOptions.gpus, populated by runpodRefreshOptions).
 * Falls back to a generic warning if no GPU is chosen yet or the catalog hasn't loaded.
 */
function cloudBackendsEstimatePriceLine(backendId) {
    let gpuId = document.getElementById(`setting_${backendId}_RunPodPods_GpuTypeId`)?.value;
    let match = gpuId && runpodOptions?.gpus ? runpodOptions.gpus.find(g => g.id == gpuId) : null;
    if (match && match.price_per_hr) {
        return `This will start a RunPod GPU Pod on ${match.name || gpuId} at roughly $${match.price_per_hr}/hr.`;
    }
    return 'This will start (or create) a RunPod GPU Pod, which bills continuously while running.';
}

/**
 * Builds the Start Pod / Stop Pod buttons and live status line for the RunPod GPU Pods section, wired
 * to CloudStartPod / CloudStopPod / CloudGetPodStatus. Returns { refresh() } so the caller can trigger
 * the first load lazily (on section expand) instead of on every card render.
 */
function cloudBackendsSetupPodActions(actionsDiv, backendId) {
    let startButton = document.createElement('button');
    startButton.className = 'basic-button cloudbackends-pod-start';
    startButton.innerText = 'Start Pod';
    let stopButton = document.createElement('button');
    stopButton.className = 'basic-button cloudbackends-pod-stop';
    stopButton.innerText = 'Stop Pod';
    let statusLine = createDiv(null, 'cloudbackends-pod-status');
    statusLine.innerText = 'Status: unknown (expand to load)';
    actionsDiv.appendChild(startButton);
    actionsDiv.appendChild(stopButton);
    actionsDiv.appendChild(statusLine);

    function setBusy(busy, message) {
        startButton.disabled = busy;
        stopButton.disabled = busy;
        if (message) {
            statusLine.innerText = message;
        }
    }

    // genericRequest only ever calls the success callback with a payload that has no "error" field
    // (see site.js: any `data.error` is diverted to the error callback as a plain string instead) - so
    // `data.success` here is just documentation, never something to branch on; only `has_pod` is a real
    // (non-error) alternate outcome worth handling in the success path.
    function refresh(forceRefresh = false) {
        genericRequest('CloudGetPodStatus', { backend_id: `${backendId}`, force_refresh: forceRefresh }, data => {
            if (!data.has_pod) {
                setBusy(false, 'Status: no pod yet - Start Pod will create one.');
                startButton.disabled = false;
                stopButton.disabled = true;
                return;
            }
            let bits = [`Status: ${data.status}`];
            if (data.gpu_id) {
                bits.push(`${data.gpu_count || 1}x ${data.gpu_id}`);
            }
            if (data.cost_per_hour) {
                bits.push(`$${data.cost_per_hour.toFixed(2)}/hr`);
            }
            if (data.status == 'RUNNING') {
                bits.push(`uptime ${cloudBackendsFormatUptime(data.uptime_seconds)}`);
            }
            statusLine.innerText = bits.join(' · ');
            let allowed = data.allowed_actions || [];
            startButton.disabled = !allowed.includes('start');
            stopButton.disabled = !allowed.includes('stop');
        }, 0, e => setBusy(false, `Status: ${e}`));
    }

    startButton.addEventListener('click', () => {
        if (!confirm(`${cloudBackendsEstimatePriceLine(backendId)}\n\nYou will be billed by RunPod for as long as it runs, until you Stop it (or a failsafe below does). Continue?`)) {
            return;
        }
        setBusy(true, 'Starting pod (this can take a while on a cold start)...');
        genericRequest('CloudStartPod', { backend_id: `${backendId}` }, () => refresh(true),
            0, e => setBusy(false, `Status: start failed - ${e}`));
    });
    stopButton.addEventListener('click', () => {
        setBusy(true, 'Stopping pod...');
        genericRequest('CloudStopPod', { backend_id: `${backendId}` }, () => refresh(true),
            0, e => setBusy(false, `Status: stop failed - ${e}`));
    });

    return { refresh };
}

// Patch the schema immediately before any cloud_backends card is drawn, then regroup it into
// per-provider sections right after. Swarm rebuilds a card from scratch on add, on save, and whenever
// the status poll sees it change, and all of those funnel through this one function.
if (typeof addBackendToHtml != 'undefined') {
    let cloudBackendsOriginalAddBackendToHtml = addBackendToHtml;
    addBackendToHtml = function(backend, disable, spot = null) {
        if (backend && backend.type == CLOUDBACKENDS_TYPE_ID) {
            runpodApplySchema();
        }
        let cardBase = cloudBackendsOriginalAddBackendToHtml(backend, disable, spot);
        if (backend && backend.type == CLOUDBACKENDS_TYPE_ID) {
            let cardBody = document.getElementById(`backend-card-${backend.id}`)?.querySelector('.card-body');
            if (cardBody) {
                cloudBackendsReorganizeCard(cardBody, backend.id);
            }
        }
        return cardBase;
    };
}

sessionReadyCallbacks.push(() => {
    runpodRefreshOptions();
    // Refresh when the user opens the Backends tab, so a GPU that sold out (or an API key added
    // since page load) is reflected without a reload.
    let tabButton = document.getElementById('serverbackendstabbutton');
    if (tabButton) {
        tabButton.addEventListener('click', () => runpodRefreshOptions(() => {
            if (typeof loadBackendsList != 'undefined') {
                loadBackendsList();
            }
        }));
    }
});
