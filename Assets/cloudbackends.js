/**
 * Two jobs for the single "Cloud Backends" settings card:
 *
 * 1. Fills the RunPod GPU Pods and Vast.ai Instances sections with live choices from the user's account:
 *    which GPUs/offers are actually rentable right now and what they cost, which network volumes exist,
 *    and (RunPod only) which data centers/templates/pods.
 *
 *    Approach: rewrite the cached settings schema in `backend_types['cloud_backends']` and let Swarm's
 *    own renderer draw the dropdowns. Swarm builds the card straight from that schema, so patching the
 *    schema means every render path (add, save, and the status poll) draws the right control with the
 *    right `data-name`, and saving keeps working untouched. Core does the same thing for VAE lists.
 *
 *    If the account call fails or no API key is set, the schema is left alone and the fields stay as
 *    plain text boxes, which still work if you know the exact ids.
 *
 * 2. Regroups the card's flat field list into one collapsible section per provider (RunPod Serverless /
 *    RunPod GPU Pods / Vast.ai Serverless / Vast.ai Instances), each with its own Enabled toggle pulled
 *    up into the section header. Purely a DOM reorganization after Swarm's normal renderer draws the
 *    flat card - every field keeps its original `data-name`, so Save/Edit/load keep working exactly as
 *    core expects. The two "rent a whole instance" sections (RunPod GPU Pods, Vast.ai Instances) also get
 *    Start/Stop buttons and a live status line, both driven by the same generalized
 *    `cloudBackendsSetupInstanceActions` - only the WebAPI call names and a few response field names
 *    differ between the two providers (see each entry's `instanceActions` in CLOUDBACKENDS_PROVIDERS).
 */

let runpodOptions = null;
let runpodOptionsPending = false;
let vastOptions = null;
let vastOptionsPending = false;

const CLOUDBACKENDS_TYPE_ID = 'cloud_backends';

/**
 * One entry per provider section, in the order fields are declared in CloudBackendsBackend.Settings.
 * `instanceActions` is only present on the two "rent a whole instance" providers (RunPod GPU Pods,
 * Vast.ai Instances) - its presence is what triggers Start/Stop buttons + a status line for that section.
 */
const CLOUDBACKENDS_PROVIDERS = [
    { prefix: 'RunPodServerless_', label: 'RunPod Serverless' },
    {
        prefix: 'RunPodPods_', label: 'RunPod GPU Pods',
        instanceActions: {
            itemLabel: 'Pod', article: 'a',
            startCall: 'CloudStartPod', stopCall: 'CloudStopPod', statusCall: 'CloudGetPodStatus',
            hasKey: 'has_pod', gpuKey: 'gpu_id', uptimeKey: 'uptime_seconds', uptimeUnit: 'seconds',
            hasAllowedActions: true, noItemMessage: 'no pod yet - Start Pod will create one.',
            pickerField: 'RunPodPods_GpuTypeId',
            priceLookup: val => runpodOptions?.gpus?.find(g => g.id == val),
            priceField: 'price_per_hr', nameField: 'name'
        }
    },
    { prefix: 'VastAI_', label: 'Vast.ai Serverless' },
    {
        prefix: 'VastAIInstance_', label: 'Vast.ai Instances',
        instanceActions: {
            itemLabel: 'Instance', article: 'an',
            startCall: 'VastAIStartInstance', stopCall: 'VastAIStopInstance', statusCall: 'VastAIGetInstanceStatus',
            hasKey: 'has_instance', gpuKey: 'gpu_name', uptimeKey: 'uptime_minutes', uptimeUnit: 'minutes',
            // Vast has no RunPod-style "actions" list on its instance object (see VastAIInstanceStatus.cs),
            // so there's nothing to gate button availability on beyond having a real status at all.
            hasAllowedActions: false, noItemMessage: 'no instance yet - Start Instance will create one.',
            pickerField: 'VastAIInstance_OfferId',
            priceLookup: val => vastOptions?.offers?.find(o => o.id == val),
            priceField: 'dph_total', nameField: 'gpu_name'
        }
    }
];

/** Fields that become dropdowns, mapped to a builder over the fetched RunPod options. */
function runpodBuildChoices() {
    if (!runpodOptions) {
        return null;
    }
    let gpus = cloudBackendsWithBlank(runpodOptions.gpus, '(cheapest available)',
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
    let volumes = cloudBackendsWithBlank(runpodOptions.network_volumes, '(no network volume)',
        v => v.id,
        v => `${v.name || v.id} · ${v.size_gb || '?'}GB${v.data_center ? ' · ' + v.data_center : ''}`);
    let dcs = cloudBackendsWithBlank(runpodOptions.data_centers, '(let RunPod choose)',
        d => d.id,
        d => d.name && d.name != d.id ? `${d.id} · ${d.name}` : d.id);
    let templates = cloudBackendsWithBlank(runpodOptions.templates, '(none, use an image)',
        t => t.id,
        t => t.image ? `${t.name || t.id} · ${t.image}` : (t.name || t.id));
    let pods = cloudBackendsWithBlank(runpodOptions.pods, '(create or reuse by name)',
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

/** Fields that become dropdowns, mapped to a builder over the fetched Vast.ai options. */
function vastBuildChoices() {
    if (!vastOptions) {
        return null;
    }
    let offers = cloudBackendsWithBlank(vastOptions.offers, '(cheapest available)',
        o => o.id,
        o => {
            let bits = [`${o.num_gpus || 1}x ${o.gpu_name || o.id}`];
            if (o.gpu_ram) {
                bits.push(`${o.gpu_ram}GB`);
            }
            if (o.dph_total) {
                bits.push(`$${o.dph_total.toFixed(2)}/hr`);
            }
            if (o.geolocation) {
                bits.push(o.geolocation);
            }
            if (o.reliability) {
                bits.push(`${Math.round(o.reliability * 100)}% reliable`);
            }
            return bits.join(' · ');
        });
    let volumes = cloudBackendsWithBlank(vastOptions.network_volumes, '(no network volume)',
        v => v.id,
        v => `${v.name || v.id} · ${v.size_gb || '?'}GB`);
    return {
        'VastAIInstance_OfferId': offers,
        'VastAIInstance_NetworkVolumeId': volumes
    };
}

/** Shared blank-first-entry list builder used by both {run,vast}BuildChoices. */
function cloudBackendsWithBlank(items, blankLabel, valueOf, labelOf) {
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

/** Rewrites the cardBody's cached cloud_backends schema so Swarm renders live dropdowns instead of text boxes. */
function cloudBackendsApplyChoicesToSchema(choices) {
    let type = typeof backend_types != 'undefined' ? backend_types[CLOUDBACKENDS_TYPE_ID] : null;
    if (!type || !type.settings || !choices) {
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

function runpodApplySchema() {
    cloudBackendsApplyChoicesToSchema(runpodBuildChoices());
}

function vastApplySchema() {
    cloudBackendsApplyChoicesToSchema(vastBuildChoices());
}

/** Fetches the account's RunPod options, then repaints any visible cloud_backends cards. */
function runpodRefreshOptions(then = null) {
    if (runpodOptionsPending) {
        return;
    }
    runpodOptionsPending = true;
    // genericRequest only ever calls the success callback with a payload that has no "error" field
    // (see site.js: any `data.error` is diverted to the error callback as a plain string instead), so a
    // missing key or a failed call always lands in the error callback below, never here as `data.error`.
    genericRequest('CloudListRunPodOptions', { 'cloud_type': 'SECURE' }, data => {
        runpodOptionsPending = false;
        runpodOptions = data;
        runpodApplySchema();
        if (then) {
            then();
        }
    }, 0, e => {
        runpodOptionsPending = false;
        console.log(`[CloudBackends] Could not load RunPod options, leaving text inputs: ${e}`);
    });
}

/** Fetches the account's Vast.ai options, then repaints any visible cloud_backends cards. */
function vastRefreshOptions(then = null) {
    if (vastOptionsPending) {
        return;
    }
    vastOptionsPending = true;
    genericRequest('VastAIListInstanceOptions', {}, data => {
        vastOptionsPending = false;
        vastOptions = data;
        vastApplySchema();
        if (then) {
            then();
        }
    }, 0, e => {
        vastOptionsPending = false;
        console.log(`[CloudBackends] Could not load Vast.ai options, leaving text inputs: ${e}`);
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
        let instanceControls = provider.instanceActions ? cloudBackendsSetupInstanceActions(actions, backendId, provider.instanceActions) : null;
        let statusLoadedOnce = false;
        header.addEventListener('click', () => {
            let opening = body.style.display == 'none';
            body.style.display = opening ? 'block' : 'none';
            caret.innerText = opening ? '▼' : '▶';
            // Load status lazily on first expand, not on every render/poll, so a collapsed section never
            // spends a provider API call (the server-side status cache covers the rest: repeat expands
            // within its TTL are free).
            if (opening && instanceControls && !statusLoadedOnce) {
                statusLoadedOnce = true;
                instanceControls.refresh();
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

/** Seconds -> compact "1h05m" / "5m12s" / "42s" for an instance status line. */
function cloudBackendsFormatDurationSeconds(seconds) {
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

/** As above, but accepts either seconds (RunPod) or minutes (Vast.ai) per `cfg.uptimeUnit`. */
function cloudBackendsFormatDuration(value, unit) {
    return cloudBackendsFormatDurationSeconds(unit == 'minutes' ? value * 60 : value);
}

/**
 * Best-effort price line for the Start confirmation, read from the currently-selected GPU/offer picker
 * field against the already-fetched catalog (cfg.priceLookup). Falls back to a generic warning if
 * nothing is chosen yet or the catalog hasn't loaded.
 */
function cloudBackendsEstimatePriceLine(backendId, cfg) {
    let pickedValue = document.getElementById(`setting_${backendId}_${cfg.pickerField}`)?.value;
    let match = pickedValue ? cfg.priceLookup(pickedValue) : null;
    let price = match ? match[cfg.priceField] : null;
    if (match && price) {
        return `This will start ${cfg.article} ${cfg.itemLabel} on ${match[cfg.nameField] || pickedValue} at roughly $${(+price).toFixed(2)}/hr.`;
    }
    return `This will start (or create) ${cfg.article} ${cfg.itemLabel}, which bills continuously while running.`;
}

/**
 * Builds the Start/Stop buttons and live status line for a "rent a whole instance" section (RunPod GPU
 * Pods, Vast.ai Instances), wired to the WebAPI calls and response field names in `cfg` (see each
 * provider's `instanceActions` in CLOUDBACKENDS_PROVIDERS). Returns { refresh() } so the caller can
 * trigger the first load lazily (on section expand) instead of on every card render.
 */
function cloudBackendsSetupInstanceActions(actionsDiv, backendId, cfg) {
    let startButton = document.createElement('button');
    startButton.className = 'basic-button cloudbackends-pod-start';
    startButton.innerText = `Start ${cfg.itemLabel}`;
    let stopButton = document.createElement('button');
    stopButton.className = 'basic-button cloudbackends-pod-stop';
    stopButton.innerText = `Stop ${cfg.itemLabel}`;
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

    // Same genericRequest contract as CloudListRunPodOptions above: the success callback never sees an
    // "error"-bearing payload, so only cfg.hasKey (a real non-error alternate outcome) needs handling here.
    function refresh(forceRefresh = false) {
        genericRequest(cfg.statusCall, { backend_id: `${backendId}`, force_refresh: forceRefresh }, data => {
            if (!data[cfg.hasKey]) {
                setBusy(false, `Status: ${cfg.noItemMessage}`);
                startButton.disabled = false;
                stopButton.disabled = true;
                return;
            }
            let bits = [`Status: ${data.status}`];
            let gpu = data[cfg.gpuKey];
            if (gpu) {
                bits.push(`${data.gpu_count || 1}x ${gpu}`);
            }
            if (data.cost_per_hour) {
                bits.push(`$${data.cost_per_hour.toFixed(2)}/hr`);
            }
            let uptime = data[cfg.uptimeKey];
            if (uptime) {
                bits.push(`uptime ${cloudBackendsFormatDuration(uptime, cfg.uptimeUnit)}`);
            }
            statusLine.innerText = bits.join(' · ');
            if (cfg.hasAllowedActions) {
                let allowed = data.allowed_actions || [];
                startButton.disabled = !allowed.includes('start');
                stopButton.disabled = !allowed.includes('stop');
            }
            else {
                // No allowed-actions signal from this provider - both are always sensible once we have a real status.
                startButton.disabled = false;
                stopButton.disabled = false;
            }
        }, 0, e => setBusy(false, `Status: ${e}`));
    }

    startButton.addEventListener('click', () => {
        if (!confirm(`${cloudBackendsEstimatePriceLine(backendId, cfg)}\n\nYou will be billed for as long as it runs, until you Stop it (or a failsafe below does). Continue?`)) {
            return;
        }
        setBusy(true, `Starting ${cfg.itemLabel.toLowerCase()} (this can take a while on a cold start)...`);
        genericRequest(cfg.startCall, { backend_id: `${backendId}` }, () => refresh(true),
            0, e => setBusy(false, `Status: start failed - ${e}`));
    });
    stopButton.addEventListener('click', () => {
        setBusy(true, `Stopping ${cfg.itemLabel.toLowerCase()}...`);
        genericRequest(cfg.stopCall, { backend_id: `${backendId}` }, () => refresh(true),
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
            vastApplySchema();
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
    vastRefreshOptions();
    // Refresh when the user opens the Backends tab, so a GPU/offer that sold out (or an API key added
    // since page load) is reflected without a reload.
    let tabButton = document.getElementById('serverbackendstabbutton');
    if (tabButton) {
        tabButton.addEventListener('click', () => {
            let repaint = () => { if (typeof loadBackendsList != 'undefined') { loadBackendsList(); } };
            runpodRefreshOptions(repaint);
            vastRefreshOptions(repaint);
        });
    }
});
