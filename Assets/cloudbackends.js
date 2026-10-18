/**
 * Enhances the "Cloud Backends" card on top of core's rendering, without replacing any core code:
 * - Groups the fields into one collapsible section per provider, with an Enabled toggle and a setup guide link in each
 *   header, plain labels, and the rarely needed fields folded into an Advanced group.
 * - Turns endpoint, GPU, network volume and similar fields into dropdowns filled from the user's own account
 *   (same id and data-name, so core's Edit and Save keep working). Without a key they stay text boxes.
 * - Keeps a saved value that a dropdown doesn't list, and greys out or hides fields that don't apply.
 * - Adds Start/Stop and a status line to instance sections, and Discover, Validate and a worker list to serverless ones.
 * Core rebuilds cards on add, save and status polls, so a MutationObserver re-runs enhancement; it makes no DOM
 * writes on an already-enhanced card, so it cannot loop.
 */
class CloudBackendsHelper {

    /** Sets up empty state; init() loads the provider table. */
    constructor() {
        this.typeId = 'cloud_backends';
        /** Provider entries from CloudListProviders, or null until fetched. */
        this.providers = null;
        /** Fetched account options per source id ('runpod:SECURE', 'runpod:COMMUNITY', 'vast', 'endpoints:RunPodServerless_', 'endpoints:VastAI_'). */
        this.accountOptions = {};
        /** Source ids with an options request in flight. */
        this.optionsPending = {};
        /** True once account options have been requested at least once. */
        this.optionsRequested = false;
        /** Reentrancy guard so our own enhancement writes don't recurse through the MutationObserver. */
        this.enhancing = false;
        /** Presentation details per provider prefix - everything else about a provider comes from the server. */
        this.presentation = {
            'RunPodPods_': {
                article: 'a', noItemMessage: 'not created yet. Press Start Pod to create one.', hasAllowedActions: true,
                pickerField: 'RunPodPods_GpuTypeId', priceSource: 'runpod', priceListKey: 'gpus', idField: 'id', priceField: 'price_per_hr', nameField: 'name'
            },
            'VastAIInstance_': {
                article: 'an', noItemMessage: 'not created yet. Press Start Instance to create one.', hasAllowedActions: false,
                pickerField: 'VastAIInstance_GpuName', priceSource: 'vast', priceListKey: 'gpus', idField: 'name', priceField: 'min_price', nameField: 'name'
            }
        };
        /** Plain labels for settings, by name without the provider prefix. The saved field names don't change. */
        this.labels = {
            Enabled: 'Enabled', EndpointId: 'Endpoint', MaxWorkers: 'Max Workers', IdleSeconds: 'Idle Shutdown', StartupTimeoutSec: 'Startup Timeout',
            PodId: 'Pod', InstanceId: 'Instance', WorkerToken: 'Worker Token', DeleteOnStop: 'Delete On Stop', ImageName: 'Image', Image: 'Image',
            GpuTypeId: 'GPU', GpuName: 'GPU', ContainerDiskGb: 'Container Disk', DiskGb: 'Container Disk', NetworkVolumeId: 'Network Volume',
            CloudType: 'Cloud', DataCenterId: 'Data Center', MaxRuntimeMinutes: 'Max Runtime', MaxSpendUsd: 'Max Spend',
            TemplateId: 'Template', TemplateHashId: 'Template', GpuCount: 'GPU Count', VolumeGb: 'Pod Volume (GB)', VolumeMountPath: 'Volume Mount Path',
            SwarmUIPort: 'SwarmUI Port', PodEnv: 'Environment Variables', Env: 'Environment Variables'
        };
        /**
         * Fields that only matter when another field is set. 'hide' removes the row; 'dim' greys it out, for a field the
         * other one overrides. 'whenSet' applies the rule while the other field has a value, otherwise while it is empty.
         */
        this.conditions = [
            { field: 'RunPodPods_WorkerToken', on: 'RunPodPods_PodId', whenSet: false, effect: 'hide' },
            { field: 'VastAIInstance_WorkerToken', on: 'VastAIInstance_InstanceId', whenSet: false, effect: 'hide' },
            { field: 'RunPodPods_DataCenterId', on: 'RunPodPods_NetworkVolumeId', whenSet: true, effect: 'dim' },
            { field: 'RunPodPods_ImageName', on: 'RunPodPods_TemplateId', whenSet: true, effect: 'dim' },
            { field: 'VastAIInstance_Image', on: 'VastAIInstance_TemplateHashId', whenSet: true, effect: 'dim' }
        ];
    }

    /** Called once at session ready: fetches the provider table, then hooks re-render points. Account options load when the Backends tab opens. */
    init() {
        genericRequest('CloudListProviders', {}, data => {
            this.providers = data.providers || [];
            backendsRevisedCallbacks.push(() => this.enhanceAllCards());
            let list = document.getElementById('backends_list');
            if (list) {
                new MutationObserver(() => this.onListMutated()).observe(list, { childList: true, subtree: true });
            }
            let tabButton = document.getElementById('serverbackendstabbutton');
            document.addEventListener('change', e => {
                let name = e.target?.dataset?.name;
                if (!name || !this.providers.some(p => name.startsWith(p.prefix))) {
                    return;
                }
                // A card switched to another RunPod cloud type needs that type's prices and availability.
                if (name == 'RunPodPods_CloudType') {
                    this.refreshAllOptions();
                }
                // A different endpoint has a different worker limit, and other fields show or hide with this one.
                this.enhanceAllCards();
            });
            document.addEventListener('input', e => {
                let name = e.target?.dataset?.name;
                if (name && this.providers.some(p => name.startsWith(p.prefix))) {
                    this.applyConditions(e.target.closest('.card-body'));
                }
            });
            if (tabButton) {
                // Loaded on each visit, so what is rentable (and a key added since) stays current without a reload.
                tabButton.addEventListener('click', () => this.refreshAllOptions());
            }
            this.enhanceAllCards();
        }, 0, e => console.warn(`[CloudBackends] Could not list providers, card enhancement disabled: ${e}`));
    }

    /** Fetches both providers' account options. RunPod's are fetched once per cloud type the cards use, since prices and availability differ. */
    refreshAllOptions() {
        this.optionsRequested = true;
        let cloudTypes = new Set(['SECURE']);
        for (let select of document.querySelectorAll('[data-name="RunPodPods_CloudType"]')) {
            if (select.value) {
                cloudTypes.add(select.value);
            }
        }
        for (let cloudType of cloudTypes) {
            this.refreshOptions(`runpod:${cloudType}`, 'CloudListRunPodOptions', { 'cloud_type': cloudType });
        }
        this.refreshOptions('vast', 'VastAIListInstanceOptions', {});
        for (let prefix of ['RunPodServerless_', 'VastAI_']) {
            this.refreshOptions(`endpoints:${prefix}`, 'CloudListEndpoints', { 'provider': prefix });
        }
    }

    /** The fetched account options of one provider ('runpod' or 'vast') that apply to a card. */
    optionsFor(source, backendId) {
        if (source != 'runpod') {
            return this.accountOptions[source];
        }
        let cloudType = document.getElementById(`setting_${backendId}_RunPodPods_CloudType`)?.value || 'SECURE';
        return this.accountOptions[`runpod:${cloudType}`];
    }

    /** Re-runs enhancement after core changes the backends list, guarding against our own writes. */
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
        genericRequest(call, params, data => {
            this.optionsPending[source] = false;
            this.accountOptions[source] = data;
            this.enhanceAllCards();
        }, 0, e => {
            this.optionsPending[source] = false;
            console.warn(`[CloudBackends] Could not load ${source} options, leaving text inputs: ${e}`);
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
            if (!this.optionsRequested && cardBody.offsetParent != null) {
                // The Backends tab was already open when the page loaded, so its click never fired.
                this.optionsRequested = true;
                this.refreshAllOptions();
            }
            if (!cardBody.classList.contains('cloudbackends-organized')) {
                cardBody.classList.add('cloudbackends-organized');
                this.keepSavedValues(cardBody, backend);
                this.reorganizeCard(cardBody, backend.id);
            }
            this.applyDropdowns(cardBody, backend.id);
            this.applyConditions(cardBody);
        }
    }

    /**
     * Core renders a dropdown whose saved value isn't one of its options with the first option selected, which the next
     * Save would write back. Adds the saved value as its own entry instead, so nothing changes unless the user picks.
     */
    keepSavedValues(cardBody, backend) {
        for (let select of cardBody.querySelectorAll('select[data-name]')) {
            let saved = backend.settings?.[select.dataset.name];
            if (saved == null || saved === '') {
                continue;
            }
            let value = `${saved}`;
            if (![...select.options].some(o => o.value == value)) {
                let option = document.createElement('option');
                option.value = value;
                option.innerText = `${value} (saved value)`;
                select.appendChild(option);
            }
            select.value = value;
        }
    }

    /** Hides or greys out the fields listed in this.conditions, by the current value of the field each depends on. */
    applyConditions(cardBody) {
        if (!cardBody) {
            return;
        }
        for (let rule of this.conditions) {
            let target = cardBody.querySelector(`[data-name="${rule.field}"]`)?.closest('.auto-input');
            let on = cardBody.querySelector(`[data-name="${rule.on}"]`);
            if (!target || !on) {
                continue;
            }
            let applies = (on.value || '').trim() != '' ? rule.whenSet : !rule.whenSet;
            if (rule.effect == 'hide') {
                target.style.display = applies ? 'none' : '';
            }
            else {
                target.classList.toggle('cloudbackends-field-inactive', applies);
            }
        }
    }

    // ── Live dropdowns ───────────────────────────────────────────────────────

    /** Field name -> {values, names} choices for everything the fetched account options can offer a card. */
    buildChoices(backendId) {
        let choices = {};
        let runpod = this.optionsFor('runpod', backendId);
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
            choices['RunPodPods_TemplateId'] = this.withBlank(runpod.templates, '(none, use the image)',
                t => t.id,
                t => t.image ? `${t.name || t.id} · ${t.image}` : (t.name || t.id));
            choices['RunPodPods_PodId'] = this.withBlank(runpod.pods, '(create one for me)',
                p => p.id,
                p => `${p.name || p.id} · ${p.status || '?'}${p.gpu ? ' · ' + p.gpu : ''}`);
        }
        let vast = this.optionsFor('vast', backendId);
        if (vast) {
            choices['VastAIInstance_GpuName'] = this.withBlank(vast.gpus, '(cheapest available)',
                g => g.name,
                g => {
                    let bits = [g.name];
                    if (g.gpu_ram) {
                        bits.push(`${g.gpu_ram}GB`);
                    }
                    if (g.min_price) {
                        bits.push(`from $${g.min_price.toFixed(2)}/hr`);
                    }
                    if (g.offers) {
                        bits.push(`${g.offers} available`);
                    }
                    return bits.join(' · ');
                });
            choices['VastAIInstance_InstanceId'] = this.withBlank(vast.instances, '(create one for me)',
                i => i.id,
                i => `${i.label || i.id} · ${i.status || '?'}${i.gpu ? ` · ${i.num_gpus || 1}x ${i.gpu}` : ''}`);
            choices['VastAIInstance_TemplateHashId'] = this.withBlank(vast.templates, '(none, use the image)',
                t => t.id,
                t => t.image ? `${t.name || t.id} · ${t.image}` : (t.name || t.id));
            choices['VastAIInstance_NetworkVolumeId'] = this.withBlank(vast.network_volumes, '(no network volume)',
                v => v.id,
                v => `${v.name || v.id} · ${v.size_gb || '?'}GB`);
        }
        for (let prefix of ['RunPodServerless_', 'VastAI_']) {
            let endpoints = this.accountOptions[`endpoints:${prefix}`]?.endpoints;
            if (!endpoints) {
                continue;
            }
            choices[`${prefix}EndpointId`] = this.withBlank(endpoints, '(pick an endpoint)',
                e => e.id,
                e => e.name && e.name != e.id ? `${e.name} · ${e.id}` : e.id);
            // Offer only as many workers as the chosen endpoint allows.
            let chosen = document.getElementById(`setting_${backendId}_${prefix}EndpointId`)?.value;
            let max = endpoints.find(e => e.id == chosen)?.max_workers;
            if (max > 0) {
                let workers = { values: [], names: [] };
                for (let i = 1; i <= Math.min(max, 20); i++) {
                    workers.values.push(`${i}`);
                    workers.names.push(i == max ? `${i} (the endpoint's max)` : `${i}`);
                }
                choices[`${prefix}MaxWorkers`] = workers;
            }
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
        let choices = this.buildChoices(backendId);
        for (let [field, choice] of Object.entries(choices)) {
            // Leave the field as-is when the account has nothing to offer, so it stays typeable
            // rather than becoming a dropdown whose only entry is "none".
            if (!choice || choice.values.filter(v => v != '').length == 0) {
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
     * Labels a setting by its name without the provider prefix (EndpointId, not RunPodServerless_EndpointId), as
     * Swarm's own settings read; the section header already names the provider. Only the displayed label and help
     * title change: the input's data-name, and so saving and the API, keep the full field name.
     */
    showUnprefixedName(div, prefix) {
        let fullName = div.querySelector('[data-name]').dataset.name;
        let shortName = fullName.substring(prefix.length);
        let label = this.labels[shortName] || shortName;
        for (let span of div.querySelectorAll('.auto-input-name .translate')) {
            if (span.dataset.pretranslated == fullName) {
                span.dataset.pretranslated = label;
                span.innerText = translate(label);
            }
        }
        for (let bold of div.querySelectorAll('.sui-popover b')) {
            if (bold.innerText == fullName) {
                bold.innerText = label;
            }
        }
    }

    /** Gives a text field a list of suggestions it still lets the user type past (used for the worker image). */
    addSuggestions(div, items) {
        let field = div.querySelector('[data-name]');
        if (!field || !items?.length) {
            return;
        }
        // Core renders text settings as a textarea, which can't take a suggestion list; a one-line input with the same id,
        // data-name and value saves the same way.
        let input = field;
        if (field.tagName == 'TEXTAREA') {
            input = document.createElement('input');
            input.type = 'text';
            input.id = field.id;
            input.dataset.name = field.dataset.name;
            input.className = 'auto-text cloudbackends-suggest-input';
            input.placeholder = field.placeholder;
            input.value = field.value;
            input.disabled = field.disabled;
            input.autocomplete = 'off';
            field.replaceWith(input);
        }
        let list = document.createElement('datalist');
        list.id = `${input.id}_suggestions`;
        for (let item of items) {
            let option = document.createElement('option');
            option.value = item.image;
            option.label = item.label;
            list.appendChild(option);
        }
        input.setAttribute('list', list.id);
        div.appendChild(list);
    }

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
            for (let div of matches) {
                this.showUnprefixedName(div, provider.prefix);
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
            if (provider.guide_url) {
                let guide = document.createElement('a');
                guide.className = 'cloudbackends-guide-link';
                guide.href = provider.guide_url;
                guide.target = '_blank';
                guide.rel = 'noopener noreferrer';
                guide.innerText = 'Setup guide';
                guide.title = `Step-by-step ${provider.label} setup, from a new account to generating`;
                guide.addEventListener('click', e => e.stopPropagation());
                header.appendChild(guide);
            }
            let body = createDiv(null, 'cloudbackends-provider-body');
            body.style.display = 'none';
            if (toggleDiv) {
                // A copy of the Enabled checkbox in the header; the original stays in the body with its label.
                let toggleInput = toggleDiv.querySelector('[data-name]');
                let headerToggle = toggleInput.cloneNode(true);
                headerToggle.removeAttribute('id');
                headerToggle.removeAttribute('data-name');
                headerToggle.checked = toggleInput.checked;
                // Core's Edit enables only [data-name] inputs, so follow the original's disabled state.
                new MutationObserver(() => { headerToggle.disabled = toggleInput.disabled; }).observe(toggleInput, { attributes: true, attributeFilter: ['disabled'] });
                headerToggle.addEventListener('click', e => e.stopPropagation());
                headerToggle.addEventListener('change', () => { toggleInput.checked = headerToggle.checked; });
                toggleInput.addEventListener('change', () => { headerToggle.checked = toggleInput.checked; });
                header.appendChild(headerToggle);
                // The header toggle replaces the row; the row stays in the page so Save still reads it.
                toggleDiv.style.display = 'none';
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
            let advancedFields = provider.advanced_fields || [];
            let advanced = createDiv(null, 'cloudbackends-advanced');
            let advancedHeader = createDiv(null, 'cloudbackends-advanced-header');
            advancedHeader.innerText = '▶ Advanced';
            let advancedBody = createDiv(null, 'cloudbackends-advanced-body');
            advancedBody.style.display = 'none';
            advancedHeader.addEventListener('click', () => {
                let opening = advancedBody.style.display == 'none';
                advancedBody.style.display = opening ? 'block' : 'none';
                advancedHeader.innerText = `${opening ? '▼' : '▶'} Advanced`;
            });
            advanced.appendChild(advancedHeader);
            advanced.appendChild(advancedBody);
            for (let div of matches) {
                let input = div.querySelector('[data-name]');
                let name = input.dataset.name;
                if (name.endsWith('_SwarmUIPort')) {
                    input.min = 1;
                    input.max = 65535;
                }
                if (name.endsWith('_ImageName') || name.endsWith('_Image')) {
                    this.addSuggestions(div, provider.images);
                }
                (advancedFields.includes(name) ? advancedBody : body).appendChild(div);
            }
            if (advancedBody.children.length > 0) {
                body.appendChild(advanced);
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
        let catalog = pres ? this.optionsFor(pres.priceSource, backendId)?.[pres.priceListKey] : null;
        let match = pickedValue && catalog ? catalog.find(entry => entry[pres.idField] == pickedValue) : null;
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
        let refresh = (forceRefresh = false) => {
            genericRequest(provider.status_call, { 'backend_id': `${backendId}`, 'force_refresh': forceRefresh }, data => {
                if (!data.has_instance) {
                    setBusy(false, `Status: ${data.message || pres?.noItemMessage || 'none yet, Start creates one.'}`);
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
            let finished = false;
            let socket = makeWSRequest(provider.start_call_ws, { 'backend_id': `${backendId}` }, data => {
                if (data.status) {
                    statusLine.innerText = `Starting: ${data.status}`;
                }
                if (data.done) {
                    finished = true;
                    refresh(true);
                }
            }, 0, e => {
                finished = true;
                setBusy(false, `Status: start failed: ${e}`);
            });
            if (socket) {
                // A dropped connection must not leave the buttons disabled on "Starting...".
                socket.addEventListener('close', () => {
                    if (!finished) {
                        refresh(true);
                    }
                });
            }
        });
        stopButton.addEventListener('click', () => {
            setBusy(true, `Stopping ${itemLabel.toLowerCase()}...`);
            genericRequest(provider.stop_call, { 'backend_id': `${backendId}` }, () => refresh(true),
                0, e => setBusy(false, `Status: stop failed: ${e}`));
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
                label.innerText = `${worker.worker_id || worker.lease_id} · ${worker.in_use ? 'generating' : 'idle'} · up ${this.formatDuration(worker.age_seconds)}`;
                let stop = document.createElement('button');
                stop.className = 'basic-button cloudbackends-worker-stop';
                stop.innerText = 'Stop';
                stop.addEventListener('click', () => {
                    if (!confirm(`Release worker ${worker.worker_id} now? Anything it is generating fails.`)) {
                        return;
                    }
                    stop.disabled = true;
                    genericRequest('CloudStopWorker', { 'backend_id': `${backendId}`, 'worker_id': worker.worker_id || worker.lease_id }, () => refresh(), 0, e => {
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
            }, 0, e => console.warn(`[CloudBackends] Could not check for orphaned instances: ${e}`));
        };
        load();
    }
}

let cloudBackendsHelper = new CloudBackendsHelper();

sessionReadyCallbacks.push(() => cloudBackendsHelper.init());
