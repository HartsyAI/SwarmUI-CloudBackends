/**
 * Client-side enhancement for the "Cloud Backends" backend card, done strictly additively on top of
 * core's own rendering (no core function is replaced and no shared schema is mutated):
 *
 * 1. Live dropdowns: fields like GPU type, network volume, or Vast.ai offer become dropdowns filled
 *    from the user's own provider account (what is rentable right now, at what price). The conversion
 *    is an in-place DOM swap of the rendered text input for a select with the same id and data-name,
 *    so core's Edit/Save flow keeps working untouched. If the account call fails or no API key is set,
 *    the fields stay as plain text boxes, which still work if you know the exact ids.
 *
 * 2. Section regrouping: the card's flat field list is regrouped into one collapsible section per
 *    provider, each with its own Enabled toggle in the header. Instance-renting sections (RunPod GPU
 *    Pods, Vast.ai Instances) also get Start/Stop buttons and a live status line; Start streams the
 *    server's progress over websocket, since a cold start takes minutes.
 *
 * The provider table itself comes from the server (CloudListProviders); only presentation details
 * (wording, which field feeds the price estimate) live here.
 *
 * Re-render hooks: core rebuilds a card from scratch on add, save, and status-poll changes. Those all
 * land in the DOM, so a MutationObserver on the backends list re-runs enhancement; the
 * backendsRevisedCallbacks hook covers the regular poll path as well. Enhancement is idempotent and
 * performs zero DOM writes when a card is already enhanced, so the observer cannot loop.
 */
class CloudBackendsHelper {

    constructor() {
        this.typeId = 'cloud_backends';
        /** Provider entries from CloudListProviders, or null until fetched. */
        this.providers = null;
        /** Fetched account options per source id ('runpod', 'vast'). */
        this.accountOptions = {};
        this.optionsPending = {};
        /** Reentrancy guard so our own enhancement writes don't recurse through the MutationObserver. */
        this.enhancing = false;
        /** Presentation details per provider prefix - everything else about a provider comes from the server. */
        this.presentation = {
            'RunPodPods_': {
                article: 'a', noItemMessage: 'no pod yet - Start Pod will create one.', hasAllowedActions: true,
                pickerField: 'RunPodPods_GpuTypeId', priceSource: 'runpod', priceListKey: 'gpus', priceField: 'price_per_hr', nameField: 'name'
            },
            'VastAIInstance_': {
                article: 'an', noItemMessage: 'no instance yet - Start Instance will create one.', hasAllowedActions: false,
                pickerField: 'VastAIInstance_OfferId', priceSource: 'vast', priceListKey: 'offers', priceField: 'dph_total', nameField: 'gpu_name'
            }
        };
    }

    /** Called once at session ready: fetch the provider table and account options, then hook re-render points. */
    init() {
        genericRequest('CloudListProviders', {}, data => {
            this.providers = data.providers || [];
            this.refreshOptions('runpod', 'CloudListRunPodOptions', { 'cloud_type': 'SECURE' });
            this.refreshOptions('vast', 'VastAIListInstanceOptions', {});
            backendsRevisedCallbacks.push(() => this.enhanceAllCards());
            let list = document.getElementById('backends_list');
            if (list) {
                // Core rebuilds cards directly (bypassing the poll callback) on add and on save - the
                // observer catches those. Enhancement makes no DOM writes once a card is done, so our
                // own edits can't loop it.
                new MutationObserver(() => this.onListMutated()).observe(list, { childList: true, subtree: true });
            }
            let tabButton = document.getElementById('serverbackendstabbutton');
            if (tabButton) {
                // Refresh when the user opens the Backends tab, so a GPU/offer that sold out (or an API
                // key added since page load) is reflected without a reload.
                tabButton.addEventListener('click', () => {
                    this.refreshOptions('runpod', 'CloudListRunPodOptions', { 'cloud_type': 'SECURE' });
                    this.refreshOptions('vast', 'VastAIListInstanceOptions', {});
                });
            }
            this.enhanceAllCards();
        }, 0, e => console.log(`[CloudBackends] Could not list providers, card enhancement disabled: ${e}`));
    }

    onListMutated() {
        if (this.enhancing) {
            return;
        }
        this.enhancing = true;
        try {
            this.enhanceAllCards();
        }
        finally {
            this.enhancing = false;
        }
    }

    /** Fetches one provider's account options, then repaints any visible cloud_backends cards. */
    refreshOptions(source, call, params) {
        if (this.optionsPending[source]) {
            return;
        }
        this.optionsPending[source] = true;
        // genericRequest only ever calls the success callback with a payload that has no "error" field
        // (site.js diverts any `data.error` to the error callback as a plain string), so a missing key
        // or failed call always lands in the error callback below, never here.
        genericRequest(call, params, data => {
            this.optionsPending[source] = false;
            this.accountOptions[source] = data;
            this.enhanceAllCards();
        }, 0, e => {
            this.optionsPending[source] = false;
            console.log(`[CloudBackends] Could not load ${source} options, leaving text inputs: ${e}`);
        });
    }

    /** Enhances every rendered cloud_backends card. Zero DOM writes for already-enhanced cards. */
    enhanceAllCards() {
        if (!this.providers || typeof backends_loaded == 'undefined') {
            return;
        }
        for (let backend of Object.values(backends_loaded)) {
            if (backend.type != this.typeId) {
                continue;
            }
            let cardBody = document.getElementById(`backend-card-${backend.id}`)?.querySelector('.card-body');
            if (!cardBody) {
                continue;
            }
            this.applyDropdowns(cardBody, backend.id);
            if (!cardBody.classList.contains('cloudbackends-organized')) {
                cardBody.classList.add('cloudbackends-organized');
                this.reorganizeCard(cardBody, backend.id);
            }
        }
    }

    // ── Live dropdowns ───────────────────────────────────────────────────────

    /** Field name -> {values, names} choices for everything the fetched account options can offer. */
    buildChoices() {
        let choices = {};
        let runpod = this.accountOptions['runpod'];
        if (runpod) {
            choices['RunPodPods_GpuTypeId'] = this.withBlank(runpod.gpus, '(cheapest available)',
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
            choices['RunPodPods_NetworkVolumeId'] = this.withBlank(runpod.network_volumes, '(no network volume)',
                v => v.id,
                v => `${v.name || v.id} · ${v.size_gb || '?'}GB${v.data_center ? ' · ' + v.data_center : ''}`);
            choices['RunPodPods_DataCenterId'] = this.withBlank(runpod.data_centers, '(let RunPod choose)',
                d => d.id,
                d => d.name && d.name != d.id ? `${d.id} · ${d.name}` : d.id);
            choices['RunPodPods_TemplateId'] = this.withBlank(runpod.templates, '(none, use an image)',
                t => t.id,
                t => t.image ? `${t.name || t.id} · ${t.image}` : (t.name || t.id));
            choices['RunPodPods_PodId'] = this.withBlank(runpod.pods, '(create or reuse by name)',
                p => p.id,
                p => `${p.name || p.id} · ${p.status || '?'}${p.gpu ? ' · ' + p.gpu : ''}`);
        }
        let vast = this.accountOptions['vast'];
        if (vast) {
            choices['VastAIInstance_OfferId'] = this.withBlank(vast.offers, '(cheapest available)',
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
            choices['VastAIInstance_NetworkVolumeId'] = this.withBlank(vast.network_volumes, '(no network volume)',
                v => v.id,
                v => `${v.name || v.id} · ${v.size_gb || '?'}GB`);
        }
        return choices;
    }

    /** Shared blank-first-entry list builder. */
    withBlank(items, blankLabel, valueOf, labelOf) {
        let out = { values: [''], names: [blankLabel] };
        for (let item of items || []) {
            let val = valueOf(item);
            if (!val) {
                continue;
            }
            out.values.push(`${val}`);
            out.names.push(labelOf(item));
        }
        return out;
    }

    /** Swaps text inputs for selects (same id/data-name, preserved value) wherever choices exist. Writes nothing when already up to date. */
    applyDropdowns(cardBody, backendId) {
        let choices = this.buildChoices();
        for (let [field, choice] of Object.entries(choices)) {
            // Leave the field as-is when the account has nothing to offer, so it stays typeable
            // rather than becoming a dropdown whose only entry is "none".
            if (!choice || choice.values.length <= 1) {
                continue;
            }
            let element = cardBody.querySelector(`#setting_${backendId}_${field}`);
            if (!element) {
                continue;
            }
            let current = element.value;
            let values = [...choice.values];
            let names = [...choice.names];
            if (current && !values.includes(current)) {
                // A saved value the live list doesn't offer right now (sold out, renamed) must never be
                // silently dropped by the conversion.
                values.push(current);
                names.push(`${current} (saved value)`);
            }
            let signature = JSON.stringify([values, names]);
            if (element.tagName == 'SELECT' && element.dataset.cloudbackendsSig == signature) {
                continue;
            }
            let select = element;
            if (element.tagName != 'SELECT') {
                select = document.createElement('select');
                select.id = element.id;
                select.dataset.name = element.dataset.name;
                select.className = 'auto-dropdown';
                select.disabled = element.disabled;
                element.replaceWith(select);
            }
            select.innerHTML = '';
            for (let i = 0; i < values.length; i++) {
                let option = document.createElement('option');
                option.value = values[i];
                option.innerText = names[i];
                select.appendChild(option);
            }
            select.value = current;
            select.dataset.cloudbackendsSig = signature;
        }
    }

    // ── Section regrouping ───────────────────────────────────────────────────

    /**
     * Regroups a freshly-rendered card's flat field list into one collapsible section per provider,
     * each with its own Enabled toggle in the header. Every field div keeps its original data-name
     * input untouched (just moved in the DOM), so Save/Edit continue to work unmodified.
     */
    reorganizeCard(cardBody, backendId) {
        let settingDivs = [...cardBody.children].filter(div => div.querySelector('[data-name]'));
        for (let provider of this.providers) {
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
                // Mirror the Enabled checkbox into the header for at-a-glance state and one-click
                // toggling without expanding the section; the original stays in the body for its label.
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
            let instanceControls = provider.is_instance ? this.setupInstanceActions(actions, backendId, provider) : this.setupServerlessActions(actions, backendId, provider);
            let statusLoadedOnce = false;
            header.addEventListener('click', () => {
                let opening = body.style.display == 'none';
                body.style.display = opening ? 'block' : 'none';
                caret.innerText = opening ? '▼' : '▶';
                // Load status lazily on first expand, not on every render/poll, so a collapsed section
                // never spends a provider API call (the server-side status cache covers repeat expands).
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
                body.appendChild(div);
            }
            body.appendChild(actions);
        }
        this.setupOrphanNotice(cardBody);
    }

    // ── Instance actions (Start/Stop/status) ─────────────────────────────────

    /** Seconds -> compact "1h05m" / "5m12s" / "42s" for an instance status line. */
    formatDuration(seconds) {
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
     * Best-effort price line for the Start confirmation, read from the currently-selected GPU/offer
     * picker field against the fetched catalog. Falls back to a generic warning if nothing is chosen
     * yet or the catalog hasn't loaded.
     */
    estimatePriceLine(backendId, provider, pres) {
        let itemLabel = provider.item_label || 'Instance';
        let pickedValue = pres ? document.getElementById(`setting_${backendId}_${pres.pickerField}`)?.value : null;
        let catalog = pres ? this.accountOptions[pres.priceSource]?.[pres.priceListKey] : null;
        let match = pickedValue && catalog ? catalog.find(entry => entry.id == pickedValue) : null;
        let price = match ? match[pres.priceField] : null;
        if (match && price) {
            return `This will start ${pres.article} ${itemLabel} on ${match[pres.nameField] || pickedValue} at roughly $${(+price).toFixed(2)}/hr.`;
        }
        return `This will start (or create) ${pres?.article || 'an'} ${itemLabel}, which bills continuously while running.`;
    }

    /**
     * Builds the Start/Stop buttons and live status line for an instance-renting section, wired to the
     * routes the server declared for that provider. Start streams progress over websocket - a cold
     * start takes minutes, and a single hanging response tells the user nothing. Returns { refresh() }
     * so the caller can trigger the first load lazily (on section expand).
     */
    setupInstanceActions(actionsDiv, backendId, provider) {
        let pres = this.presentation[provider.prefix];
        let itemLabel = provider.item_label || 'Instance';
        let startButton = document.createElement('button');
        startButton.className = 'basic-button cloudbackends-pod-start';
        startButton.innerText = `Start ${itemLabel}`;
        let stopButton = document.createElement('button');
        stopButton.className = 'basic-button cloudbackends-pod-stop';
        stopButton.innerText = `Stop ${itemLabel}`;
        let statusLine = createDiv(null, 'cloudbackends-pod-status');
        statusLine.innerText = 'Status: unknown (expand to load)';
        actionsDiv.appendChild(startButton);
        actionsDiv.appendChild(stopButton);
        actionsDiv.appendChild(statusLine);
        let setBusy = (busy, message) => {
            startButton.disabled = busy;
            stopButton.disabled = busy;
            if (message) {
                statusLine.innerText = message;
            }
        };
        // Same genericRequest contract as refreshOptions above: the success callback never sees an
        // "error"-bearing payload, so only has_instance (a real non-error alternate outcome) needs
        // handling here.
        let refresh = (forceRefresh = false) => {
            genericRequest(provider.status_call, { 'backend_id': `${backendId}`, 'force_refresh': forceRefresh }, data => {
                if (!data.has_instance) {
                    setBusy(false, `Status: ${pres?.noItemMessage || 'none yet - Start will create one.'}`);
                    startButton.disabled = false;
                    stopButton.disabled = true;
                    return;
                }
                let bits = [`Status: ${data.status}`];
                if (data.gpu_name) {
                    bits.push(`${data.gpu_count || 1}x ${data.gpu_name}`);
                }
                if (data.cost_per_hour) {
                    bits.push(`$${data.cost_per_hour.toFixed(2)}/hr`);
                }
                if (data.uptime_seconds) {
                    bits.push(`uptime ${this.formatDuration(data.uptime_seconds)}`);
                }
                statusLine.innerText = bits.join(' · ');
                if (pres?.hasAllowedActions) {
                    let allowed = data.allowed_actions || [];
                    startButton.disabled = !allowed.includes('start');
                    stopButton.disabled = !allowed.includes('stop');
                }
                else {
                    // No allowed-actions signal from this provider - both are sensible once we have a real status.
                    startButton.disabled = false;
                    stopButton.disabled = false;
                }
            }, 0, e => setBusy(false, `Status: ${e}`));
        };
        startButton.addEventListener('click', () => {
            if (!confirm(`${this.estimatePriceLine(backendId, provider, pres)}\n\nYou will be billed for as long as it runs, until you Stop it (or a failsafe below does). Continue?`)) {
                return;
            }
            setBusy(true, `Starting ${itemLabel.toLowerCase()} (this can take a while on a cold start)...`);
            makeWSRequest(provider.start_call_ws, { 'backend_id': `${backendId}` }, data => {
                if (data.status) {
                    statusLine.innerText = `Starting: ${data.status}`;
                }
                if (data.done) {
                    refresh(true);
                }
            }, 0, e => setBusy(false, `Status: start failed - ${e}`));
        });
        stopButton.addEventListener('click', () => {
            setBusy(true, `Stopping ${itemLabel.toLowerCase()}...`);
            genericRequest(provider.stop_call, { 'backend_id': `${backendId}` }, () => refresh(true),
                0, e => setBusy(false, `Status: stop failed - ${e}`));
        });
        return { refresh };
    }

    // ── Serverless actions (workers, discover, validate) ─────────────────────

    /**
     * Builds the controls for a serverless section: the live list of leased workers (each with a Stop
     * button), Discover models, and Validate. Everything goes through the extension's API routes.
     * Returns { refresh() }, called on first expand so a collapsed section never calls the provider.
     */
    setupServerlessActions(actionsDiv, backendId, provider) {
        let discoverButton = document.createElement('button');
        discoverButton.className = 'basic-button cloudbackends-discover';
        discoverButton.innerText = 'Discover models';
        discoverButton.title = 'Starts a worker (billed) and learns which models it has. Needed once per endpoint if you have none of its models locally.';
        let validateButton = document.createElement('button');
        validateButton.className = 'basic-button cloudbackends-validate';
        validateButton.innerText = 'Validate';
        validateButton.title = 'Checks the endpoint\'s settings on the provider against this card. Costs nothing.';
        let refreshButton = document.createElement('button');
        refreshButton.className = 'basic-button cloudbackends-refresh';
        refreshButton.innerText = 'Refresh';
        let statusLine = createDiv(null, 'cloudbackends-pod-status');
        statusLine.innerText = 'Workers: expand to load';
        let workerList = createDiv(null, 'cloudbackends-worker-list');
        actionsDiv.appendChild(discoverButton);
        actionsDiv.appendChild(validateButton);
        actionsDiv.appendChild(refreshButton);
        actionsDiv.appendChild(statusLine);
        actionsDiv.appendChild(workerList);
        let refresh = () => {
            genericRequest('CloudListWorkers', { 'backend_id': `${backendId}`, 'provider': provider.prefix }, data => {
                this.renderWorkers(workerList, statusLine, backendId, data.backends || [], refresh);
            }, 0, e => {
                statusLine.innerText = `Workers: ${e}`;
            });
        };
        discoverButton.addEventListener('click', () => {
            if (!confirm('Discover models starts a worker if none is running, which bills until it goes idle. Continue?')) {
                return;
            }
            discoverButton.disabled = true;
            statusLine.innerText = 'Discovering models (a cold start can take a few minutes)...';
            genericRequest('CloudRefreshModels', { 'backend_id': backendId, 'provider': provider.prefix }, data => {
                discoverButton.disabled = false;
                statusLine.innerText = data.message || 'Models discovered.';
                refresh();
            }, 0, e => {
                discoverButton.disabled = false;
                statusLine.innerText = `Discover failed: ${e}`;
            });
        });
        validateButton.addEventListener('click', () => {
            statusLine.innerText = 'Validating...';
            genericRequest('CloudValidateBackend', { 'backend_id': `${backendId}` }, data => {
                let lines = [];
                for (let result of data.results || []) {
                    for (let finding of result.findings || []) {
                        lines.push(`${result.provider}: ${finding.level}: ${finding.message}`);
                    }
                }
                statusLine.innerText = lines.length == 0 ? 'Validate: no problems found.' : lines.join('\n');
            }, 0, e => {
                statusLine.innerText = `Validate failed: ${e}`;
            });
        });
        refreshButton.addEventListener('click', () => refresh());
        return { refresh };
    }

    /** Renders the leased-worker list for one serverless section. */
    renderWorkers(listDiv, statusLine, backendId, backends, refresh) {
        listDiv.innerHTML = '';
        let total = 0;
        for (let backend of backends) {
            for (let worker of backend.workers || []) {
                total++;
                let row = createDiv(null, 'cloudbackends-worker-row');
                let label = document.createElement('span');
                label.innerText = `${worker.worker_id} · ${worker.in_use ? 'generating' : 'idle'} · up ${this.formatDuration(worker.age_seconds)}`;
                let stop = document.createElement('button');
                stop.className = 'basic-button cloudbackends-worker-stop';
                stop.innerText = 'Stop';
                stop.addEventListener('click', () => {
                    if (!confirm(`Release worker ${worker.worker_id} now? Anything it is generating fails.`)) {
                        return;
                    }
                    stop.disabled = true;
                    genericRequest('CloudStopWorker', { 'backend_id': `${backendId}`, 'worker_id': worker.worker_id }, () => refresh(), 0, e => {
                        stop.disabled = false;
                        statusLine.innerText = `Stop failed: ${e}`;
                    });
                });
                row.appendChild(label);
                row.appendChild(stop);
                listDiv.appendChild(row);
            }
        }
        let starting = backends.reduce((sum, b) => sum + (b.workers_starting || 0), 0);
        let max = backends.reduce((sum, b) => sum + (b.max_workers || 0), 0);
        statusLine.innerText = total == 0 && starting == 0 ? 'Workers: none running (they start when you generate).' : `Workers: ${total} running${starting ? `, ${starting} starting` : ''} (max ${max}).`;
    }

    // ── Orphans ──────────────────────────────────────────────────────────────

    /** Shows a notice on the card if the user has pods/instances this extension created that nothing has attached. */
    setupOrphanNotice(cardBody) {
        if (cardBody.querySelector('.cloudbackends-orphans')) {
            return;
        }
        let notice = createDiv(null, 'cloudbackends-orphans');
        notice.style.display = 'none';
        cardBody.prepend(notice);
        let load = () => {
            genericRequest('CloudListOrphans', {}, data => {
                let orphans = data.orphans || [];
                notice.innerHTML = '';
                notice.style.display = orphans.length == 0 ? 'none' : 'block';
                if (orphans.length == 0) {
                    return;
                }
                let title = createDiv(null, 'cloudbackends-orphans-title');
                title.innerText = `${orphans.length} cloud machine(s) created by Cloud Backends are running but not attached to any card. They bill until stopped.`;
                notice.appendChild(title);
                for (let orphan of orphans) {
                    let row = createDiv(null, 'cloudbackends-worker-row');
                    let label = document.createElement('span');
                    label.innerText = `${orphan.provider}: ${orphan.name} (${orphan.id})${orphan.cost_per_hr ? ` · $${(+orphan.cost_per_hr).toFixed(2)}/hr` : ''}`;
                    let stop = document.createElement('button');
                    stop.className = 'basic-button cloudbackends-worker-stop';
                    stop.innerText = 'Stop';
                    stop.addEventListener('click', () => {
                        stop.disabled = true;
                        genericRequest('CloudStopOrphan', { 'provider': orphan.provider, 'resource_id': orphan.id }, () => load(), 0, e => {
                            stop.disabled = false;
                            label.innerText += ` · stop failed: ${e}`;
                        });
                    });
                    row.appendChild(label);
                    row.appendChild(stop);
                    notice.appendChild(row);
                }
            }, 0, e => console.log(`[CloudBackends] Could not check for orphaned instances: ${e}`));
        };
        load();
    }
}

let cloudBackendsHelper = new CloudBackendsHelper();

sessionReadyCallbacks.push(() => cloudBackendsHelper.init());
