/**
 * Fills the RunPod Pods backend settings with live choices from the user's RunPod account:
 * which GPUs are actually rentable right now and what they cost, which network volumes exist,
 * which data centers, which templates, and which pods are already running.
 *
 * Approach: rewrite the cached settings schema in `backend_types` and let Swarm's own renderer
 * draw the dropdowns. Swarm builds each backend card straight from that schema, so patching the
 * schema means every render path (add, save, and the status poll) draws the right control with the
 * right `data-name`, and saving keeps working untouched. Core does the same thing for VAE lists.
 *
 * If the RunPod call fails or no API key is set, the schema is left alone and the fields stay as
 * plain text boxes, which still work if you know the exact ids.
 */

let runpodOptions = null;
let runpodOptionsPending = false;

/** Fields that become dropdowns, mapped to a builder over the fetched options. */
function runpodBuildChoices() {
    if (!runpodOptions) {
        return null;
    }
    let blank = { values: [''], names: ['(none)'] };
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
        'GpuTypeId': gpus,
        'NetworkVolumeId': volumes,
        'DataCenterId': dcs,
        'TemplateId': templates,
        'PodId': pods,
        'GpuCount': { values: ['1', '2', '4', '8'], names: ['1', '2', '4', '8'] },
        'CloudType': { values: ['SECURE', 'COMMUNITY'], names: ['Secure Cloud', 'Community Cloud'] }
    };
}

/** Rewrites the cached runpod_pods schema so Swarm renders live dropdowns instead of text boxes. */
function runpodApplySchema() {
    let type = typeof backend_types != 'undefined' ? backend_types['runpod_pods'] : null;
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

/** Fetches the account's options, then repaints any visible pod backends. */
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

// Patch the schema immediately before any pod backend card is drawn. Swarm rebuilds a card from
// scratch on add, on save, and whenever the status poll sees it change, and all of those funnel
// through this one function, so this is the only place that needs to know.
if (typeof addBackendToHtml != 'undefined') {
    let runpodOriginalAddBackendToHtml = addBackendToHtml;
    addBackendToHtml = function(backend, disable, spot = null) {
        if (backend && backend.type == 'runpod_pods') {
            runpodApplySchema();
        }
        return runpodOriginalAddBackendToHtml(backend, disable, spot);
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
