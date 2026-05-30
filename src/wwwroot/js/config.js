// @ts-check
// ── Type definitions ──────────────────────────────────────────────────────

/** @typedef {{ value: string, label: string }} ConfigOption */
/** @typedef {{ key: string, label: string, type: string, value: unknown, nullable?: boolean, options?: (string | ConfigOption)[], description?: string, defaultTemplate?: string }} ConfigField */
/** @typedef {{ key: string, label: string, isProvider?: boolean, fields: ConfigField[] }} ConfigSection */
/** @typedef {{ sectionKey: string, fieldKey: string }} PasswordFieldRef */
/** @typedef {'ok' | 'wrong-password' | 'error'} VaultUnlockResult */
/** @typedef {'browser' | 'session'} VaultUnlockSource */
/** @typedef {Record<string, Record<string, unknown>>} SectionValueMap */
/** @typedef {{ exists: boolean }} SecretExistsResponse */
/** @typedef {{ secret: string }} SecretLoadResponse */
/** @typedef {{ unlocked: boolean }} VaultStatusResponse */
/** @typedef {{ entries: Record<string, string> }} VaultExportEntriesResponse */
/** @typedef {{ sections: ConfigSection[] }} ConfigSchemaResponse */
/** @typedef {{ type: string, vendor: string, vendorId: number, deviceId: number, metadata: Record<string, string> }} OnnxRuntimeHardwareDevice */
/** @typedef {{ epName: string, epVendor: string, suggestedDeviceId?: number | null, epMetadata: Record<string, string>, epOptions: Record<string, string>, hardwareDevice: OnnxRuntimeHardwareDevice }} OnnxRuntimeEpDevice */
/** @typedef {{ ortVersion?: string | null, availableProviders?: string[], hardwareDevices?: OnnxRuntimeHardwareDevice[], epDevices?: OnnxRuntimeEpDevice[], error?: string | null }} OnnxRuntimeCapabilitiesResponse */

const STORAGE_KEY  = 'tua_config_v1';
const VAULT_KEY    = 'tua_vault_hash_v1'; // stored only when "remember" is checked
const HUMAN_ONLY_BUILD_CACHE_KEY = 'tua_is_human_only_build';

/** @type {ConfigSection[]} */
let serverDefaults = []; // raw schema sections from /api/config/schema
/** @type {SectionValueMap} */
let browserValues  = {}; // persisted overrides { sectionKey: { fieldKey: value } }
/** @type {SectionValueMap} */
let pendingChanges = {}; // unsaved UI edits { sectionKey: { fieldKey: value } }
/** @type {OnnxRuntimeCapabilitiesResponse | null} */
let onnxRuntimeCapabilities = null;
/** @type {boolean} */
let onnxCapabilitiesProbeAttempted = false;
/** @type {boolean} */
let onnxCapabilitiesRequestInFlight = false;

/** @type {string | null} */
let vaultPasswordHash  = null;  // SHA-256 hex provided by the browser this page visit
let vaultSessionActive = false; // true when server already has the hash from a prior unlock

/**
 * @returns {Promise<boolean>}
 */
async function checkSessionHumanOnlyBuild() {
    const cached = sessionStorage.getItem(HUMAN_ONLY_BUILD_CACHE_KEY);
    if (cached !== null) {
        return cached === 'true';
    }

    try {
        const res = await fetch('/api/HumanOnlyBuild');
        const data = await res.json();
        const isHumanOnly = data === true || data?.isHumanOnly === true;
        sessionStorage.setItem(HUMAN_ONLY_BUILD_CACHE_KEY, String(isHumanOnly));
        return isHumanOnly;
    } catch {
        sessionStorage.setItem(HUMAN_ONLY_BUILD_CACHE_KEY, 'false');
        return false;
    }
}

/** @returns {void} */
function lockHumanOnlyModeField() {
    const input = findInput('general', 'humanControlOnlyMode');
    if (!(input instanceof HTMLInputElement) || input.type !== 'checkbox') return;

    input.checked = true;
    input.disabled = true;
    input.title = 'Locked on because this build is Human Control Only.';
    input.classList.remove('changed');

    pendingChanges.general && delete pendingChanges.general.humanControlOnlyMode;
    if (pendingChanges.general && Object.keys(pendingChanges.general).length === 0) {
        delete pendingChanges.general;
    }

    markBrowserOverride('general', 'humanControlOnlyMode', false);
}

// ── Vault helpers ─────────────────────────────────────────────────────────

/**
 * @param {string} password
 * @returns {Promise<string>} SHA-256 hex digest
 */
async function hashPassword(password) {
    const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(password));
    return Array.from(new Uint8Array(buf)).map(b => b.toString(16).padStart(2, '0')).join('');
}

/**
 * @returns {PasswordFieldRef[]}
 */
function getPasswordFields() {
    const result = [];
    for (const section of serverDefaults) {
        for (const field of section.fields) {
            if (field.type === 'password') result.push({ sectionKey: section.key, fieldKey: field.key });
        }
    }
    return result;
}

/**
 * @param {string} sectionKey
 * @param {string} fieldKey
 * @returns {string}
 */
function secretKeyFor(sectionKey, fieldKey) {
    return `${sectionKey}_${fieldKey}`;
}

/**
 * Checks /api/secrets/{key}/exists for every password field and updates placeholders.
 * @returns {Promise<void>}
 */
async function updatePasswordPlaceholders() {
    for (const { sectionKey, fieldKey } of getPasswordFields()) {
        const input = findInput(sectionKey, fieldKey);
        if (!input) continue;
        try {
            const res = await fetch(`/api/secrets/${encodeURIComponent(secretKeyFor(sectionKey, fieldKey))}/exists`);
            if (res.ok) {
                const { exists } = await res.json();
                if (!vaultPasswordHash && input instanceof HTMLInputElement) {
                    input.placeholder = exists ? '(encrypted — unlock vault to load)' : '(not set)';
                }
            }
        } catch { /* best-effort */ }
    }
}

/**
 * Attempts to unlock the vault with the given password hash.
 * Loads each secret from the backend, pushes plaintext to /api/config for the current session,
 * and updates password field placeholders to reflect loaded state.
 * @param {string | null} hash SHA-256 hex of the vault password, or null to use the server session
 * @returns {Promise<VaultUnlockResult>}
 */
async function tryUnlockVault(hash) {
    // hash may be null when the server already holds the session hash.
    const passwordFields = getPasswordFields();
    /** @type {SectionValueMap} */
    const serverUpdates  = {};
    let   wrongPassword  = false;
    let   anyLoaded      = false;

    for (const { sectionKey, fieldKey } of passwordFields) {
        const key = secretKeyFor(sectionKey, fieldKey);

        // Skip keys that have never been saved — avoids a noisy 404 in the console.
        try {
            const existsRes = await fetch(`/api/secrets/${encodeURIComponent(key)}/exists`);
            if (existsRes.ok) {
                const { exists } = await existsRes.json();
                if (!exists) continue;
            }
        } catch { return 'error'; }

        let res;
        try {
            res = await fetch('/api/secrets/load', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                // Omit passwordHash when null — server will use its session hash.
                body:    JSON.stringify({ keyName: key, passwordHash: hash ?? null }),
            });
        } catch { return 'error'; }

        if (res.ok) {
            const { secret } = await res.json();
            serverUpdates[sectionKey] ??= {};
            serverUpdates[sectionKey][fieldKey] = secret;
            const input = findInput(sectionKey, fieldKey);
            if (input instanceof HTMLInputElement) input.placeholder = '(set — enter to change)';
            anyLoaded = true;
        } else if (res.status === 401) {
            wrongPassword = true;
            break;
        }
        // 400 = vault not unlocked server-side either; treat as wrong-password
        else if (res.status === 400) {
            wrongPassword = true;
            break;
        }
        // 404 = not yet saved; leave placeholder as-is
    }

    if (wrongPassword) return 'wrong-password';

    vaultPasswordHash = hash;       // null when using server session
    vaultSessionActive = (hash === null && anyLoaded);
    if (Object.keys(serverUpdates).length > 0) {
        try { await pushToServer(serverUpdates); } catch { /* best-effort */ }
    }
    return 'ok';
}

/**
 * Checks the server-side vault status and, if unlocked, loads secrets via the stored session hash.
 * @returns {Promise<void>}
 */
async function checkAndApplyServerVaultSession() {
    try {
        const res = await fetch('/api/secrets/vault/status');
        if (!res.ok) { setStatus('Browser overrides loaded'); return; }
        const { unlocked } = await res.json();
        if (unlocked) {
            const result = await tryUnlockVault(null);
            if (result === 'ok') {
                setVaultUnlocked(true, 'session');
                setStatus('Vault session active');
                return;
            }
        }
    } catch { /* best-effort */ }
    setStatus('Browser overrides loaded');
}

/**
 * Renders the vault section and inserts it into the left column.
 * @returns {void}
 */
function renderVaultSection() {
    const wrap = document.createElement('div');
    wrap.className = 'vault-section';
    wrap.id = 'vault-section';

    const hdr = document.createElement('div');
    hdr.className = 'vault-header';
    hdr.innerHTML =
        '<span class="vault-title">🔑 API Key Vault</span>' +
        '<span class="vault-badge" id="vault-badge">Locked</span>';
    wrap.appendChild(hdr);

    const body = document.createElement('div');
    body.className = 'vault-body';

    const info = document.createElement('div');
    info.className = 'vault-info';
    info.textContent =
        'API keys are encrypted and stored on the server. ' +
        'Your vault password is hashed in the browser — it never leaves your device. ' +
        'The hash can optionally be remembered in browser storage to auto-unlock on next visit.';
    body.appendChild(info);

    const row = document.createElement('div');
    row.className = 'vault-row';

    // Password input
    const pwWrap = document.createElement('div');
    pwWrap.className = 'vault-pw-wrap';
    const pwInput = document.createElement('input');
    pwInput.type        = 'password';
    pwInput.id          = 'vault-pw-input';
    pwInput.placeholder = 'Vault password';
    pwInput.addEventListener('keydown', e => { if (e.key === 'Enter') btnUnlock.click(); });
    const showBtn = document.createElement('button');
    showBtn.className = 'show-pw-btn';
    showBtn.type      = 'button';
    showBtn.textContent = '👁';
    showBtn.title = 'Show / hide';
    showBtn.addEventListener('click', () => { pwInput.type = pwInput.type === 'password' ? 'text' : 'password'; });
    pwWrap.appendChild(pwInput);
    pwWrap.appendChild(showBtn);
    row.appendChild(pwWrap);

    // Remember checkbox
    const remLabel = document.createElement('label');
    remLabel.className = 'vault-remember';
    const remCheck = document.createElement('input');
    remCheck.type    = 'checkbox';
    remCheck.id      = 'vault-remember';
    remCheck.title   = 'Store the password hash in browser storage so the vault unlocks automatically next time';
    remCheck.checked = !!localStorage.getItem(VAULT_KEY);
    remCheck.addEventListener('change', () => {
        // Keep the lock button label in sync when toggled while vault is unlocked.
        const lockBtn = document.getElementById('btn-vault-lock');
        if (lockBtn && lockBtn.style.display !== 'none')
            lockBtn.textContent = remCheck.checked ? 'Lock & Forget' : 'Lock';
    });
    remLabel.appendChild(remCheck);
    remLabel.appendChild(document.createTextNode(' Remember hash'));
    row.appendChild(remLabel);

    // Unlock button
    const btnUnlock = document.createElement('button');
    btnUnlock.className   = 'btn btn-primary';
    btnUnlock.id          = 'btn-vault-unlock';
    btnUnlock.type        = 'button';
    btnUnlock.textContent = 'Unlock';
    btnUnlock.addEventListener('click', async () => {
        const pw = pwInput.value;
        if (!pw) { showToast('Enter a vault password', true); return; }
        btnUnlock.disabled = true;
        btnUnlock.textContent = 'Unlocking…';
        const hash   = await hashPassword(pw);
        const result = await tryUnlockVault(hash);
        btnUnlock.disabled = false;
        btnUnlock.textContent = 'Unlock';
        if (result === 'wrong-password') {
            showToast('Wrong password', true);
        } else if (result === 'error') {
            showToast('Error contacting server', true);
        } else {
            if (remCheck.checked) localStorage.setItem(VAULT_KEY, hash);
            else                  localStorage.removeItem(VAULT_KEY);
            setVaultUnlocked(true, 'browser');
            pwInput.value = '';
            showToast('Vault unlocked');
        }
    });
    row.appendChild(btnUnlock);

    // Lock button (hidden until unlocked)
    const btnLock = document.createElement('button');
    btnLock.className   = 'btn';
    btnLock.id          = 'btn-vault-lock';
    btnLock.type        = 'button';
    btnLock.textContent = 'Lock';
    btnLock.style.display = 'none';
    btnLock.addEventListener('click', async () => {
        const hadRemembered = !!localStorage.getItem(VAULT_KEY);
        vaultPasswordHash  = null;
        vaultSessionActive = false;
        localStorage.removeItem(VAULT_KEY);
        const remCb = /** @type {HTMLInputElement | null} */ (document.getElementById('vault-remember'));
        if (remCb) remCb.checked = false;
        for (const { sectionKey, fieldKey } of getPasswordFields()) {
            const input = findInput(sectionKey, fieldKey);
            if (input) {
                input.value = '';
                if (input instanceof HTMLInputElement) input.placeholder = '(encrypted — unlock vault to load)';
            }
        }
        setVaultUnlocked(false);
        showToast(hadRemembered ? 'Vault locked & hash forgotten' : 'Vault locked');
        try { await fetch('/api/secrets/vault/lock', { method: 'POST' }); } catch { /* best-effort */ }
    });
    row.appendChild(btnLock);

    body.appendChild(row);
    wrap.appendChild(body);

    const colLeft = document.getElementById('col-left');
    if (!colLeft) return;
    colLeft.insertBefore(wrap, colLeft.firstChild);
}

/**
 * @param {boolean} on
 * @param {VaultUnlockSource} [source]
 * @returns {void}
 */
function setVaultUnlocked(on, source = 'browser') {
    const section = document.getElementById('vault-section');
    const badge   = document.getElementById('vault-badge');
    const unlock  = document.getElementById('btn-vault-unlock');
    const lock    = document.getElementById('btn-vault-lock');
    const input   = /** @type {HTMLInputElement | null} */ (document.getElementById('vault-pw-input'));
    if (!section) return;
    section.classList.toggle('unlocked', on && source === 'browser');
    section.classList.toggle('session',  on && source === 'session');
    if (badge)  badge.textContent    = on ? (source === 'session' ? 'Session Active' : 'Unlocked') : 'Locked';
    if (unlock) unlock.style.display = on ? 'none' : '';
    if (lock) {
        lock.style.display = on ? '' : 'none';
        if (on) lock.textContent = localStorage.getItem(VAULT_KEY) ? 'Lock & Forget' : 'Lock';
    }
    if (input)  input.disabled       = on;
}

// ── Boot ──────────────────────────────────────────────────────────────────

/** @returns {Promise<void>} */
async function init() {
    browserValues = loadFromStorage();
    const isHumanOnlyBuild = await checkSessionHumanOnlyBuild();

    try {
        const schemaRes = await fetch('/api/config/schema');
        if (!schemaRes.ok) throw new Error('Schema fetch failed');
        const { sections } = await schemaRes.json();
        serverDefaults = sections;
        renderSections(sections);
        renderVaultSection();
        applyStoredValues();
        if (isHumanOnlyBuild) {
            lockHumanOnlyModeField();
        }
        syncOnnxDeviceSelectionState();

        // Push all stored browser values (non-password) to the server.
        if (Object.keys(browserValues).length > 0) {
            try { await pushToServer(browserValues); } catch { /* best-effort */ }
        }

        // Check which API keys have been saved to the vault and update placeholders.
        await updatePasswordPlaceholders();

        // Auto-unlock if the user has a remembered hash.
        const storedHash = localStorage.getItem(VAULT_KEY);
        if (storedHash) {
            const result = await tryUnlockVault(storedHash);
            if (result === 'ok') {
                setVaultUnlocked(true, 'browser');
                setStatus('Vault auto-unlocked');
            } else {
                // Stored hash is stale (password changed) — clear it, then check server session.
                localStorage.removeItem(VAULT_KEY);
                await checkAndApplyServerVaultSession();
            }
        } else {
            await checkAndApplyServerVaultSession();
        }
    } catch (err) {
        const errMsg = err instanceof Error ? err.message : String(err);
        const mainEl = document.getElementById('config-main');
        if (mainEl) mainEl.textContent = 'Failed to load config schema: ' + errMsg;
    }
}

// ── Render ────────────────────────────────────────────────────────────────

/**
 * @param {ConfigSection[]} sections
 * @param {OnnxRuntimeCapabilitiesResponse | null} capabilities
 * @returns {void}
 */
function applyOnnxDynamicChoices(sections, capabilities) {
    if (!capabilities) return;

    const onnxSection = sections.find(section => section.key === 'onnx');
    if (!onnxSection) return;

    const providerField = onnxSection.fields.find(field => field.key === 'executionProvider');
    if (providerField) {
        const providerValue = findInput('onnx', 'executionProvider')?.value ?? providerField.value;
        providerField.options = buildOnnxProviderOptions(capabilities, providerValue);
    }

    const deviceField = onnxSection.fields.find(field => field.key === 'deviceId');
    if (deviceField) {
        const deviceValue = findInput('onnx', 'deviceId')?.value ?? deviceField.value;
        deviceField.options = buildOnnxDeviceOptions(capabilities, deviceValue);
    }
}

/**
 * @param {OnnxRuntimeCapabilitiesResponse} capabilities
 * @param {unknown} currentValue
 * @returns {ConfigOption[]}
 */
function buildOnnxProviderOptions(capabilities, currentValue) {
    /** @type {ConfigOption[]} */
    const options = [
        { value: 'FollowConfig', label: 'FollowConfig' },
    ];

    const availableProviders = new Set(
        [
            ...(capabilities.availableProviders || []),
            ...((capabilities.epDevices || []).map(device => device.epName)),
        ].map(normalizeProviderName)
    );

    const candidates = [
        { value: 'CPU', ortNames: ['CPU', 'CPUExecutionProvider'], label: 'CPU' },
        { value: 'DML', ortNames: ['DML', 'DMLExecutionProvider', 'DirectML', 'DirectMLExecutionProvider'], label: 'DML (DirectML)' },
        { value: 'CUDA', ortNames: ['CUDA', 'CUDAExecutionProvider'], label: 'CUDA' },
        { value: 'OpenVINO', ortNames: ['OpenVINO', 'OpenVINOExecutionProvider'], label: 'OpenVINO' },
        { value: 'QNN', ortNames: ['QNN', 'QNNExecutionProvider'], label: 'QNN' },
        { value: 'WebGPU', ortNames: ['WebGPU', 'WebGPUExecutionProvider'], label: 'WebGPU' },
        { value: 'NvTensorRtRtx', ortNames: ['NvTensorRtRtx', 'NvTensorRtRtxExecutionProvider', 'TensorRT', 'TensorRTExecutionProvider'], label: 'NvTensorRtRtx' },
    ];

    for (const candidate of candidates) {
        if (candidate.ortNames.some(name => availableProviders.has(normalizeProviderName(name)))) {
            options.push({ value: candidate.value, label: candidate.label });
        }
    }

    const current = currentValue == null ? null : String(currentValue);
    if (current && !options.some(option => option.value === current)) {
        options.push({ value: current, label: `${current} (current, unavailable)` });
    }

    return options;
}

/**
 * @param {OnnxRuntimeCapabilitiesResponse} capabilities
 * @param {unknown} currentValue
 * @returns {ConfigOption[]}
 */
function buildOnnxDeviceOptions(capabilities, currentValue) {
    /** @type {Map<string, ConfigOption>} */
    const options = new Map();

    for (const epDevice of (capabilities.epDevices || [])) {
        const suggestedId = epDevice.suggestedDeviceId
            ?? tryReadSuggestedDeviceId(epDevice.epOptions)
            ?? tryReadSuggestedDeviceId(epDevice.epMetadata)
            ?? tryReadSuggestedDeviceId(epDevice.hardwareDevice.metadata);
        if (suggestedId === null) continue;

        const description = epDevice.hardwareDevice.metadata?.Description || epDevice.hardwareDevice.vendor || 'Unknown device';
        const key = String(suggestedId);
        if (!options.has(key)) {
            options.set(key, {
                value: key,
                label: `device_id ${key} (DxgiAdapterNumber ${key}) - ${description} via ${epDevice.epName}`,
            });
        }
    }

    for (const hardwareDevice of (capabilities.hardwareDevices || [])) {
        if (hardwareDevice.type === 'CPU') continue;

        const adapterId = tryReadSuggestedDeviceId(hardwareDevice.metadata);
        if (adapterId === null) continue;

        const description = hardwareDevice.metadata?.Description || hardwareDevice.vendor || 'Unknown device';
        const key = String(adapterId);
        if (!options.has(key)) {
            options.set(key, {
                value: key,
                label: `device_id ${key} (DxgiAdapterNumber ${key}) - ${description}`,
            });
        }
    }

    const current = currentValue == null ? null : String(currentValue);
    if (current && !options.has(current)) {
        options.set(current, {
            value: current,
            label: `${current} (current, not detected)`,
        });
    }

    return Array.from(options.values()).sort((left, right) => Number.parseInt(left.value, 10) - Number.parseInt(right.value, 10));
}

/** @returns {void} */
function syncOnnxDeviceSelectionState() {
    const providerInput = findInput('onnx', 'executionProvider');
    const deviceInput = findInput('onnx', 'deviceId');
    if (!(providerInput instanceof HTMLSelectElement) || !(deviceInput instanceof HTMLSelectElement)) return;

    const provider = providerInput.value;
    const usesDeviceId = new Set(['DML', 'CUDA', 'OpenVINO', 'QNN', 'WebGPU', 'NvTensorRtRtx']).has(provider);

    deviceInput.disabled = false;
    deviceInput.dataset.inactive = usesDeviceId ? 'false' : 'true';
    deviceInput.title = usesDeviceId
        ? ''
        : 'This selection is currently informational only. It will be ignored until you select a provider that supports device selection.';

    const fieldRow = deviceInput.closest('.field-row');
    if (!fieldRow) return;

    let hint = /** @type {HTMLDivElement | null} */ (fieldRow.querySelector('.field-inline-hint'));
    if (!hint) {
        hint = document.createElement('div');
        hint.className = 'field-inline-hint';
        fieldRow.appendChild(hint);
    }

    if (usesDeviceId) {
        hint.textContent = 'The selected provider can use this device ID.';
        hint.classList.remove('muted');
    } else {
        hint.textContent = 'You can browse device IDs here, but the current provider will ignore this selection.';
        hint.classList.add('muted');
    }
}

/**
 * @param {string} sectionKey
 * @param {string} fieldKey
 * @returns {void}
 */
function syncSelectOptions(sectionKey, fieldKey) {
    const field = findFieldMeta(sectionKey, fieldKey);
    const input = findInput(sectionKey, fieldKey);
    if (!field || !(input instanceof HTMLSelectElement)) return;

    const currentValue = input.value;
    input.innerHTML = '';

    if (field.nullable) {
        const emptyOption = document.createElement('option');
        emptyOption.value = '';
        emptyOption.textContent = '— server default —';
        input.appendChild(emptyOption);
    }

    for (const option of (field.options || [])) {
        const el = document.createElement('option');
        if (typeof option === 'string') {
            el.value = option;
            el.textContent = option;
        } else {
            el.value = option.value;
            el.textContent = option.label;
        }

        input.appendChild(el);
    }

    input.value = currentValue;
    if (input.value !== currentValue) {
        setInputValue(input, field.value, field.type);
    }
}

/** @returns {void} */
function refreshOnnxCapabilitiesPanel() {
    const panel = document.getElementById('onnx-capabilities-panel');
    if (!(panel instanceof HTMLDivElement)) return;
    renderOnnxCapabilitiesPanel(panel);
}

/** @returns {Promise<void>} */
async function probeOnnxCapabilities() {
    if (onnxCapabilitiesRequestInFlight) return;

    onnxCapabilitiesProbeAttempted = true;
    onnxCapabilitiesRequestInFlight = true;
    refreshOnnxCapabilitiesPanel();

    try {
        const response = await fetch('/api/config/onnx/capabilities');
        if (response.ok) {
            onnxRuntimeCapabilities = await response.json();
        } else {
            onnxRuntimeCapabilities = {
                ortVersion: null,
                availableProviders: [],
                hardwareDevices: [],
                epDevices: [],
                error: `Capability probe failed with HTTP ${response.status}.`,
            };
        }
    } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        onnxRuntimeCapabilities = {
            ortVersion: null,
            availableProviders: [],
            hardwareDevices: [],
            epDevices: [],
            error: `Capability request failed: ${message}`,
        };
    } finally {
        onnxCapabilitiesRequestInFlight = false;
    }

    applyOnnxDynamicChoices(serverDefaults, onnxRuntimeCapabilities);
    syncSelectOptions('onnx', 'executionProvider');
    syncSelectOptions('onnx', 'deviceId');
    syncOnnxDeviceSelectionState();
    refreshOnnxCapabilitiesPanel();
}

/**
 * @param {ConfigSection[]} sections
 * @returns {void}
 */
function renderSections(sections) {
    const colLeft = document.getElementById('col-left');
    const colRight = document.getElementById('col-right');
    const colBottom = document.getElementById('col-bottom');
    if (!colLeft || !colRight || !colBottom) return;

    if (onnxRuntimeCapabilities) {
        applyOnnxDynamicChoices(sections, onnxRuntimeCapabilities);
    }

    colLeft.innerHTML = '';
    colRight.innerHTML = '';
    colBottom.innerHTML = '';

    let activeProviderField = null;

    for (const section of sections) {
        let fieldsToRender = section.fields;
        let systemPromptField = null;

        // Extract the System Prompt from General so we can isolate it into the full-width column
        if (section.key === 'general') {
            systemPromptField = section.fields.find(f => f.key === 'systemPromptTemplate');
            activeProviderField = section.fields.find(f => f.key === 'activeProvider') ?? null;
            fieldsToRender = section.fields.filter(f => f.key !== 'systemPromptTemplate' && f.key !== 'activeProvider');
        }

        // Use native collapsible <details> tag for providers
        const wrap = document.createElement(section.isProvider ? 'details' : 'div');
        wrap.className = 'config-section';
        // Note: The 'open' attribute is purposefully omitted here so provider sections start collapsed.
        wrap.dataset.section = section.key;

        // Header
        const hdr = document.createElement(section.isProvider ? 'summary' : 'div');
        hdr.className = 'section-header';
        hdr.innerHTML = `<span class="section-title">${escHtml(section.label)}</span>`;
        if (section.isProvider) {
            hdr.innerHTML += `<span class="provider-badge">AI Provider</span>`;
            hdr.innerHTML += `<span class="collapse-icon">▼</span>`;
        }
        wrap.appendChild(hdr);

        // Fields grid
        const grid = document.createElement('div');
        grid.className = 'fields-grid';

        for (const field of fieldsToRender) {
            const row = buildFieldRow(section.key, field);
            grid.appendChild(row);
        }

        wrap.appendChild(grid);

        if (section.key === 'onnx') {
            wrap.appendChild(buildOnnxCapabilitiesPanel());
        }
        
        // Push to appropriate column
        if (section.isProvider) {
            colRight.appendChild(wrap);
        } else {
            colLeft.appendChild(wrap);
        }

        // Render system prompt in its own full width wrapper at the bottom
        if (systemPromptField) {
            const spWrap = document.createElement('div');
            spWrap.className = 'config-section';
            spWrap.dataset.section = section.key;

            const spHdr = document.createElement('div');
            spHdr.className = 'section-header';
            spHdr.innerHTML = `<span class="section-title">System Prompt</span>`;
            spWrap.appendChild(spHdr);

            const spGrid = document.createElement('div');
            spGrid.className = 'fields-grid';
            const spRow = buildFieldRow(section.key, systemPromptField);
            spGrid.appendChild(spRow);
            spWrap.appendChild(spGrid);

            colBottom.appendChild(spWrap);
        }
    }

    if (activeProviderField) {
        colRight.insertBefore(buildActiveProviderSection(activeProviderField), colRight.firstChild);
    }
}

/**
 * @param {ConfigField} field
 * @returns {HTMLDivElement}
 */
function buildActiveProviderSection(field) {
    const wrap = document.createElement('div');
    wrap.className = 'config-section';
    wrap.dataset.role = 'active-provider-section';

    const hdr = document.createElement('div');
    hdr.className = 'section-header';
    hdr.innerHTML = '<span class="section-title">Provider Selection</span>';
    wrap.appendChild(hdr);

    const grid = document.createElement('div');
    grid.className = 'fields-grid';
    grid.appendChild(buildFieldRow('general', field));
    wrap.appendChild(grid);

    return wrap;
}

/**
 * @param {string} sectionKey
 * @param {ConfigField} field
 * @returns {HTMLDivElement}
 */
function buildFieldRow(sectionKey, field) {
    const row = document.createElement('div');
    row.className = 'field-row'
    row.dataset.section = sectionKey;
    row.dataset.field   = field.key;

    // label
    const labelEl = document.createElement('div');
    labelEl.className = 'field-label';
    labelEl.textContent = field.label;

    const badge = document.createElement('span');
    badge.className = 'field-browser-badge';
    badge.textContent = 'browser';
    badge.style.display = 'none';
    badge.title = 'Value overridden in browser storage';
    labelEl.appendChild(badge);
    row.appendChild(labelEl);

    // description
    if (field.description) {
        const desc = document.createElement('div');
        desc.className = 'field-desc';
        desc.textContent = field.description;
        row.appendChild(desc);
    }

    // input
    const inputWrap = document.createElement('div');
    inputWrap.className = 'field-input-wrap';

    let input;

    const hasSelectableOptions = Array.isArray(field.options) && field.options.length > 0;

    if (field.type === 'bool') {
        input = document.createElement('input');
        input.type = 'checkbox';
    } else if (field.type === 'enum' || hasSelectableOptions) {
        input = document.createElement('select');
        if (field.nullable) {
            const opt = document.createElement('option');
            opt.value = '';
            opt.textContent = '— server default —';
            input.appendChild(opt);
        }
        for (const opt of (field.options || [])) {
            const el = document.createElement('option');
            if (typeof opt === 'string') {
                el.value = opt;
                el.textContent = opt;
            } else {
                el.value = opt.value;
                el.textContent = opt.label;
            }
            input.appendChild(el);
        }
    } else {
        input = document.createElement('input');
        input.type  = field.type === 'password' ? 'password'
                    : field.type === 'int'      ? 'number'
                    : field.type === 'float'    ? 'number'
                    : 'text';
        if (field.type === 'float') { input.step = 'any'; input.min = '0'; }
        if (field.type === 'int')   { input.step = '1';   input.min = '0'; }
        input.placeholder = field.nullable ? 'server default' : '';
    }

    input.dataset.section = sectionKey;
    input.dataset.field   = field.key;

    // populate with server value
    setInputValue(input, field.value, field.type);

    input.addEventListener('change', /** @type {EventListener} */ (onFieldChange));
    if (input.type !== 'checkbox') input.addEventListener('input', /** @type {EventListener} */(onFieldChange));

    inputWrap.appendChild(input);

    // show/hide toggle for passwords
    if (field.type === 'password') {
        const btn = document.createElement('button');
        btn.className = 'show-pw-btn';
        btn.type = 'button';
        btn.textContent = '👁';
        btn.title = 'Show / hide';
        btn.addEventListener('click', () => {
            const inp = /** @type {HTMLInputElement} */ (input);
            inp.type = inp.type === 'password' ? 'text' : 'password';
        });
        inputWrap.appendChild(btn);
    }

    row.appendChild(inputWrap);

    // ── prompt-template special editor (replaces generic inputWrap for this type) ── //
    if (field.type === 'prompt-template') {
        // Remove the generic inputWrap already appended above
        row.removeChild(inputWrap);

        const editorWrap = document.createElement('div');
        editorWrap.className = 'prompt-editor-wrap';

        // Notice banner
        const notice = document.createElement('div');
        notice.className = 'prompt-editor-notice';
        notice.innerHTML =
            'Required placeholders — do not rename or remove: ' +
            ['systemInfo','goal','maxQueueSize','normalizeSize']
                .map(p => `<span class="ph">{${p}}</span>`).join(' ');
        editorWrap.appendChild(notice);

        // Scroller container (backdrop + textarea)
        const scroller = document.createElement('div');
        scroller.className = 'prompt-editor-scroller';
        scroller.dataset.section = sectionKey;
        scroller.dataset.field   = field.key;

        const backdrop = document.createElement('div');
        backdrop.className = 'prompt-backdrop';
        backdrop.setAttribute('aria-hidden', 'true');

        const textarea = document.createElement('textarea');
        textarea.className = 'prompt-textarea';
        textarea.dataset.section = sectionKey;
        textarea.dataset.field   = field.key;
        textarea.spellcheck      = false;
        textarea.autocomplete    = 'off';

        /** @returns {void} */
        function syncBackdrop() {
            const raw = escHtml(textarea.value);
            backdrop.innerHTML = raw.replace(/\{(\w+)\}/g, '<mark>{$1}</mark>');
            // Keep backdrop scroll in sync
            backdrop.scrollTop  = textarea.scrollTop;
            backdrop.scrollLeft = textarea.scrollLeft;
        }

        const initialVal = String(field.value ?? field.defaultTemplate ?? '');
        textarea.value = initialVal;
        syncBackdrop();

        textarea.addEventListener('input',  () => { syncBackdrop(); onFieldChange(/** @type {Event} */ (/** @type {unknown} */ ({ target: textarea }))); });
        textarea.addEventListener('scroll', () => { backdrop.scrollTop = textarea.scrollTop; });

        scroller.appendChild(backdrop);
        scroller.appendChild(textarea);
        editorWrap.appendChild(scroller);

        // Action row
        const actionRow = document.createElement('div');
        actionRow.className = 'prompt-editor-actions';

        const resetBtn = document.createElement('button');
        resetBtn.className = 'btn-xs';
        resetBtn.type = 'button';
        resetBtn.textContent = 'Reset to default';
        resetBtn.title = 'Restore the built-in default system prompt';
        resetBtn.addEventListener('click', () => {
            textarea.value = field.defaultTemplate ?? '';
            syncBackdrop();
            scroller.classList.remove('changed');
            // Mark as null (use default) in pending changes
            const section = textarea.dataset.section;
            const key     = textarea.dataset.field;
            if (section && key) {
                pendingChanges[section] ??= {};
                pendingChanges[section][key] = null;
            }
            setStatus('Unsaved changes');
        });
        actionRow.appendChild(resetBtn);
        editorWrap.appendChild(actionRow);

        row.appendChild(editorWrap);
        return row;
    }

    return row;
}

/**
 * @returns {HTMLDivElement}
 */
function buildOnnxCapabilitiesPanel() {
    const panel = document.createElement('div');
    panel.className = 'onnx-capabilities';
    panel.id = 'onnx-capabilities-panel';

    renderOnnxCapabilitiesPanel(panel);
    return panel;
}

/**
 * @param {HTMLDivElement} panel
 * @returns {void}
 */
function renderOnnxCapabilitiesPanel(panel) {
    panel.replaceChildren();

    const title = document.createElement('div');
    title.className = 'onnx-capabilities-title';
    title.textContent = 'Detected ONNX Runtime Capabilities';
    panel.appendChild(title);

    const intro = document.createElement('div');
    intro.className = 'onnx-capabilities-text';
    intro.textContent = onnxRuntimeCapabilities
        ? 'This is what the installed ONNX Runtime reports right now. Providers are inference backends, while provider/device pairs are a specific backend attached to a specific hardware device. If a provider is missing here, requesting it in the ONNX config will fail.'
        : 'Capability detection is manual because some environments can terminate the process when ONNX Runtime initializes. The rest of the config page does not require this probe.';
    panel.appendChild(intro);

    const capabilities = onnxRuntimeCapabilities;
    if (!capabilities) {
        const caution = document.createElement('div');
        caution.className = 'onnx-capabilities-text';
        caution.textContent = 'Run detection only on a machine where you expect ONNX Runtime to work. This is mainly for refining the provider and device dropdowns.';
        panel.appendChild(caution);
        panel.appendChild(buildOnnxCapabilitiesActions(onnxCapabilitiesProbeAttempted ? 'Retry detection' : 'Detect capabilities'));
        return;
    }

    if (capabilities.error) {
        const error = document.createElement('div');
        error.className = 'onnx-capabilities-text onnx-capabilities-error';
        error.textContent = capabilities.error;
        panel.appendChild(error);
        panel.appendChild(buildOnnxCapabilitiesActions('Retry detection'));
        return;
    }

    appendOnnxCapabilitiesBlock(panel, 'Runtime version', capabilities.ortVersion || 'unknown');

    const providers = capabilities.availableProviders || [];
    appendOnnxCapabilitiesBlock(panel, 'Available providers', providers.length ? providers.join(', ') : 'none reported');

    const epDevices = capabilities.epDevices || [];
    if (epDevices.length) {
        appendOnnxCapabilitiesList(
            panel,
            'Provider/device pairs',
            epDevices.map(describeOnnxEpDevice));
    }

    const hardwareDevices = (capabilities.hardwareDevices || []).filter(device => device.type !== 'CPU');
    if (hardwareDevices.length) {
        appendOnnxCapabilitiesList(
            panel,
            'Hardware devices',
            hardwareDevices.map(describeOnnxHardwareDevice));
    }

    panel.appendChild(buildOnnxCapabilitiesActions('Refresh capabilities'));
}

/**
 * @param {string} buttonLabel
 * @returns {HTMLDivElement}
 */
function buildOnnxCapabilitiesActions(buttonLabel) {
    const actions = document.createElement('div');
    actions.className = 'onnx-capabilities-actions';

    const button = document.createElement('button');
    button.className = onnxRuntimeCapabilities ? 'btn' : 'btn btn-primary';
    button.type = 'button';
    button.disabled = onnxCapabilitiesRequestInFlight;
    button.textContent = onnxCapabilitiesRequestInFlight ? 'Detecting…' : buttonLabel;
    button.addEventListener('click', () => { void probeOnnxCapabilities(); });
    actions.appendChild(button);

    return actions;
}

/**
 * @param {HTMLDivElement} panel
 * @param {string} label
 * @param {string} value
 * @returns {void}
 */
function appendOnnxCapabilitiesBlock(panel, label, value) {
    const group = document.createElement('div');
    group.className = 'onnx-capabilities-group';

    const labelEl = document.createElement('div');
    labelEl.className = 'onnx-capabilities-label';
    labelEl.textContent = label;

    const valueEl = document.createElement('div');
    valueEl.className = 'onnx-capabilities-text';
    valueEl.textContent = value;

    group.appendChild(labelEl);
    group.appendChild(valueEl);
    panel.appendChild(group);
}

/**
 * @param {HTMLDivElement} panel
 * @param {string} label
 * @param {string[]} items
 * @returns {void}
 */
function appendOnnxCapabilitiesList(panel, label, items) {
    const group = document.createElement('div');
    group.className = 'onnx-capabilities-group';

    const labelEl = document.createElement('div');
    labelEl.className = 'onnx-capabilities-label';
    labelEl.textContent = label;

    const list = document.createElement('ul');
    list.className = 'onnx-capabilities-list';
    for (const item of items) {
        const li = document.createElement('li');
        li.textContent = item;
        list.appendChild(li);
    }

    group.appendChild(labelEl);
    group.appendChild(list);
    panel.appendChild(group);
}

/**
 * @param {OnnxRuntimeEpDevice} epDevice
 * @returns {string}
 */
function describeOnnxEpDevice(epDevice) {
    const hardware = epDevice.hardwareDevice;
    const base = `${epDevice.epName} on ${hardware.type} ${hardware.vendor || 'Unknown Vendor'} (${formatPciId(hardware.vendorId)}:${formatPciId(hardware.deviceId)})`;
    const suggestedId = epDevice.suggestedDeviceId ?? tryReadSuggestedDeviceId(epDevice.epOptions) ?? tryReadSuggestedDeviceId(epDevice.epMetadata) ?? tryReadSuggestedDeviceId(hardware.metadata);
    const options = formatKeyValueEntries(epDevice.epOptions);
    const metadata = formatKeyValueEntries(epDevice.epMetadata);

    const parts = [base];
    if (suggestedId !== null) parts.push(`suggested device_id=${suggestedId}`);
    if (options) parts.push(`options: ${options}`);
    if (metadata) parts.push(`metadata: ${metadata}`);
    return parts.join(' | ');
}

/**
 * @param {OnnxRuntimeHardwareDevice} hardwareDevice
 * @returns {string}
 */
function describeOnnxHardwareDevice(hardwareDevice) {
    const base = `${hardwareDevice.type} ${hardwareDevice.vendor || 'Unknown Vendor'} (${formatPciId(hardwareDevice.vendorId)}:${formatPciId(hardwareDevice.deviceId)})`;
    const metadata = formatKeyValueEntries(hardwareDevice.metadata);
    return metadata ? `${base} | metadata: ${metadata}` : base;
}

/**
 * @param {Record<string, string> | undefined} entries
 * @returns {string}
 */
function formatKeyValueEntries(entries) {
    const pairs = Object.entries(entries || {}).filter(([, value]) => !!value);
    if (!pairs.length) return '';
    return pairs
        .slice(0, 5)
        .map(([key, value]) => `${key}=${value}`)
        .join(', ');
}

/**
 * @param {Record<string, string> | undefined} entries
 * @returns {number | null}
 */
function tryReadSuggestedDeviceId(entries) {
    if (!entries) return null;
    for (const key of ['device_id', 'deviceId', 'adapter_index', 'adapterIndex', 'adapter_id', 'adapterId', 'DxgiAdapterNumber', 'dxgiAdapterNumber']) {
        const raw = entries[key];
        if (raw === undefined) continue;
        const parsed = Number.parseInt(raw, 10);
        if (!Number.isNaN(parsed)) return parsed;
    }
    return null;
}

/**
 * @param {number} value
 * @returns {string}
 */
function formatPciId(value) {
    return value.toString(16).toUpperCase().padStart(4, '0');
}

/**
 * @param {string} providerName
 * @returns {string}
 */
function normalizeProviderName(providerName) {
    return providerName
        .replace(/ExecutionProvider/gi, '')
        .replace(/[_-]/g, '')
        .trim()
        .toUpperCase();
}

// ── Apply stored browser values ───────────────────────────────────────────

/** @returns {void} */
function applyStoredValues() {
    for (const [sectionKey, fields] of Object.entries(browserValues)) {
        for (const [fieldKey, value] of Object.entries(fields)) {
            const field = findFieldMeta(sectionKey, fieldKey);
            if (field?.type === 'password') continue; // managed by vault, not localStorage
            const input = findInput(sectionKey, fieldKey);
            if (!input) continue;
            setInputValue(input, value, field?.type ?? 'string');
            markBrowserOverride(sectionKey, fieldKey, true);
        }
    }
}

// ── Change tracking ───────────────────────────────────────────────────────

/**
 * @param {Event} e
 * @returns {void}
 */
function onFieldChange(e) {
    const input   = /** @type {HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement} */ (e.target);
    const section = input.dataset.section;
    const key     = input.dataset.field;
    if (!section || !key) return;
    const field   = findFieldMeta(section, key);

    const value = getInputValue(input, field?.type ?? 'string');

    pendingChanges[section] ??= {};
    pendingChanges[section][key] = value;
    // For textarea (prompt-template), mark the scroller wrapper; for others, mark the input itself
    if (input.tagName === 'TEXTAREA') {
        input.closest('.prompt-editor-scroller')?.classList.add('changed');
    } else {
        input.classList.add('changed');
    }

    if (section === 'onnx' && key === 'executionProvider') {
        syncOnnxDeviceSelectionState();
    }
    setStatus('Unsaved changes');
}

// ── Save ──────────────────────────────────────────────────────────────────

/** @returns {Promise<void>} */
async function saveSettings() {
    if (Object.keys(pendingChanges).length === 0) {
        showToast('Nothing to save');
        return;
    }

    // Separate password fields (vault) from regular fields (localStorage).
    /** @type {SectionValueMap} */
    const regularChanges  = {};
    /** @type {SectionValueMap} */
    const secretChanges   = {};
    let   hasLockedSecret = false;

    for (const [s, fields] of Object.entries(pendingChanges)) {
        for (const [k, v] of Object.entries(/** @type {Record<string, unknown>} */ (fields))) {
            const meta = findFieldMeta(s, k);
            if (meta?.type === 'password') {
                if (v) { // only queue non-empty password changes
                    if (!vaultPasswordHash && !vaultSessionActive) { hasLockedSecret = true; }
                    else { secretChanges[s] ??= {}; secretChanges[s][k] = v; }
                }
            } else {
                regularChanges[s] ??= {};
                regularChanges[s][k] = v;
            }
        }
    }

    if (hasLockedSecret) {
        showToast('Unlock vault to save API keys', true);
        // Still continue saving regular fields below.
    }

    // ── Regular fields → localStorage + /api/config ───────────────────────
    if (Object.keys(regularChanges).length > 0) {
        for (const [s, fields] of Object.entries(regularChanges)) {
            browserValues[s] ??= {};
            for (const [k, v] of Object.entries(/** @type {Record<string, unknown>} */ (fields))) {
                if (v === null || v === undefined || v === '') delete browserValues[s][k];
                else browserValues[s][k] = v;
            }
        }
        persistToStorage(browserValues);
        try { await pushToServer(regularChanges); } catch { /* best-effort */ }
    }

    // ── Secret fields → vault backend + /api/config (plaintext, in-memory) ─
    if (Object.keys(secretChanges).length > 0) {
        /** @type {SectionValueMap} */
        const serverUpdates = {};
        for (const [s, fields] of Object.entries(secretChanges)) {
            for (const [k, v] of Object.entries(/** @type {Record<string, unknown>} */ (fields))) {
                try {
                    await fetch('/api/secrets/save', {
                        method:  'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body:    JSON.stringify({ keyName: secretKeyFor(s, k), secret: v, passwordHash: vaultPasswordHash }),
                    });
                } catch { /* best-effort */ }
                serverUpdates[s] ??= {};
                serverUpdates[s][k] = v;
                // Update placeholder to reflect that the key is now set
                const inp = findInput(s, k);
                if (inp) {
                    inp.value = '';
                    if (inp instanceof HTMLInputElement) inp.placeholder = '(set — enter to change)';
                }
            }
        }
        // Push decrypted value to server in-memory config for the current session
        try { await pushToServer(serverUpdates); } catch { /* best-effort */ }
    }

    // ── Clear changed indicators for all pending fields ───────────────────
    // Locked password fields (vault not unlocked) are kept in pendingChanges so
    // a second Save attempt doesn't falsely report "Nothing to save".
    /** @type {SectionValueMap} */
    const retainedPending = {};
    for (const [s, fields] of Object.entries(pendingChanges)) {
        for (const [k, v] of Object.entries(/** @type {Record<string, unknown>} */ (fields))) {
            const meta = findFieldMeta(s, k);
            const isLockedSecret = meta?.type === 'password' && v && !vaultPasswordHash && !vaultSessionActive;
            if (isLockedSecret) {
                retainedPending[s] ??= {};
                retainedPending[s][k] = v;
                continue; // leave the changed indicator on the input
            }
            if (meta?.type !== 'password') {
                const v2 = browserValues[s]?.[k];
                markBrowserOverride(s, k, v2 !== null && v2 !== undefined && v2 !== '');
            }
            const inp = findInput(s, k);
            if (inp?.tagName === 'TEXTAREA') inp.closest('.prompt-editor-scroller')?.classList.remove('changed');
            else inp?.classList.remove('changed');
        }
    }

    syncLegacyKeys();
    pendingChanges = retainedPending;
    if (!hasLockedSecret) showToast('Saved');
    setStatus('Saved');
}

document.getElementById('btn-save')?.addEventListener('click', saveSettings);

/**
 * @param {SectionValueMap} changes
 * @returns {Promise<void>}
 */
async function pushToServer(changes) {
    // POST only the sections that changed
    /** @type {SectionValueMap} */
    const payload = {};
    for (const [s, fields] of Object.entries(changes)) {
        payload[s] = {};
        for (const [k, v] of Object.entries(fields)) {
            payload[s][k] = v;
        }
    }
    await fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
    });
}

/** @returns {void} */
function syncLegacyKeys() {
    const apiKey = browserValues?.gemini?.apiKey;
    const model  = browserValues?.gemini?.model;
    if (apiKey !== undefined) localStorage.setItem('gemini_api_key', String(apiKey ?? ''));
    if (model  !== undefined) localStorage.setItem('agent_model',    String(model  ?? ''));
}

// ── Export ────────────────────────────────────────────────────────────────

document.getElementById('btn-export')?.addEventListener('click', async () => {
    // Build a clean export from stored browser values
    /** @type {Record<string, any>} */
    const exportObj = {};
    for (const section of serverDefaults) {
        exportObj[section.label] = {};
        for (const field of section.fields) {
            const stored = browserValues[section.key]?.[field.key];
            const value  = stored !== undefined ? stored : field.value;
            if (field.type !== 'password') {
                exportObj[section.label][field.label] = value;
            }
            // Password fields are never written to the export as plaintext
        }
    }

    // Optionally embed raw encrypted vault entries
    if ((/** @type {HTMLInputElement | null} */ (document.getElementById('chk-export-vault')))?.checked) {
        try {
            const res = await fetch('/api/secrets/vault/export-entries');
            if (res.ok) {
                const { entries } = await res.json();
                if (entries && Object.keys(entries).length > 0) {
                    exportObj['_vault'] = entries;
                } else {
                    showToast('No encrypted keys found in vault', true);
                }
            } else {
                showToast('Could not read vault entries', true);
            }
        } catch {
            showToast('Error reading vault entries', true);
        }
    }

    const blob = new Blob([JSON.stringify(exportObj, null, 2)], { type: 'application/json' });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href     = url;
    a.download = 'tua-config.json';
    a.click();
    URL.revokeObjectURL(url);
});

// ── Import ────────────────────────────────────────────────────────────────

document.getElementById('btn-import')?.addEventListener('click', () => {
    document.getElementById('file-import')?.click();
});

document.getElementById('file-import')?.addEventListener('change', async (e) => {
    const target = /** @type {HTMLInputElement} */ (e.target);
    const file = target.files?.[0];
    if (!file) return;
    target.value = ''; // reset so the same file can be re-imported

    let importedObj;
    try {
        importedObj = JSON.parse(await file.text());
    } catch {
        alert('Invalid JSON file.');
        return;
    }

    // Import encrypted vault entries if present — written straight to the vault (still encrypted)
    let vaultImported = false;
    if (importedObj['_vault'] && typeof importedObj['_vault'] === 'object') {
        const entries = importedObj['_vault'];
        try {
            const res = await fetch('/api/secrets/vault/import-entries', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ entries }),
            });
            if (res.ok) {
                vaultImported = true;
                await updatePasswordPlaceholders();
            } else {
                showToast('Failed to import encrypted keys', true);
            }
        } catch {
            showToast('Error importing encrypted keys', true);
        }
    }

    // Build label → key lookup maps from serverDefaults
    /** @type {Record<string, ConfigSection>} */
    const sectionByLabel = {};
    for (const section of serverDefaults) {
        sectionByLabel[section.label] = section;
    }

    let count = 0;
    for (const [sectionLabel, fields] of Object.entries(importedObj)) {
        if (sectionLabel === '_vault') continue; // already handled above
        const section = sectionByLabel[sectionLabel];
        if (!section) continue;

        /** @type {Record<string, ConfigField>} */
        const fieldByLabel = {};
        for (const field of section.fields) {
            fieldByLabel[field.label] = field;
        }

        for (const [fieldLabel, value] of Object.entries(/** @type {Record<string, unknown>} */ (fields))) {
            const field = fieldByLabel[fieldLabel];
            if (!field) continue;
            // Password fields are handled via _vault — skip any placeholder strings
            if (field.type === 'password') continue;
            if (value === null || value === undefined) continue;

            const input = findInput(section.key, field.key);
            if (!input) continue;

            setInputValue(input, value, field.type);
            pendingChanges[section.key] ??= {};
            pendingChanges[section.key][field.key] = value;

            if (input.tagName === 'TEXTAREA') {
                input.closest('.prompt-editor-scroller')?.classList.add('changed');
            } else {
                input.classList.add('changed');
            }
            count++;
        }
    }

    if (count > 0) {
        await saveSettings();
        if (vaultImported) showToast('Imported — unlock vault to use API keys');
        else               showToast('Imported & Saved');
    } else if (vaultImported) {
        showToast('Encrypted keys imported — unlock vault to use them');
    } else {
        setStatus('Nothing to import');
        showToast('Nothing to import');
    }
});

// ── Reset ─────────────────────────────────────────────────────────────────

document.getElementById('btn-reset')?.addEventListener('click', () => {
    document.getElementById('modal-overlay')?.classList.add('show');
});
document.getElementById('modal-cancel')?.addEventListener('click', () => {
    document.getElementById('modal-overlay')?.classList.remove('show');
});

/**
 * @param {boolean} eraseSecrets
 * @returns {Promise<void>}
 */
async function performReset(eraseSecrets) {
    document.getElementById('modal-overlay')?.classList.remove('show');

    // Erase encrypted secret files from the server if requested
    if (eraseSecrets) {
        for (const { sectionKey, fieldKey } of getPasswordFields()) {
            try {
                await fetch(`/api/secrets/${encodeURIComponent(secretKeyFor(sectionKey, fieldKey))}`, { method: 'DELETE' });
            } catch { /* best-effort */ }
        }
    }

    // Clear browser storage
    localStorage.removeItem(STORAGE_KEY);
    localStorage.removeItem(VAULT_KEY);
    localStorage.removeItem('gemini_api_key');
    localStorage.removeItem('agent_model');
    browserValues     = {};
    pendingChanges    = {};
    vaultPasswordHash = null;

    // Reload server defaults and re-render
    try {
        const res = await fetch('/api/config/schema');
        const { sections } = await res.json();
        serverDefaults = sections;
        renderSections(sections);
        renderVaultSection();
        setVaultUnlocked(false);
        await updatePasswordPlaceholders();
        showToast(eraseSecrets ? 'Reset — API keys erased' : 'Reset to defaults');
        setStatus('All browser overrides cleared');
    } catch {
        location.reload();
    }
}

document.getElementById('modal-confirm')?.addEventListener('click', () => performReset(false));
document.getElementById('modal-confirm-erase')?.addEventListener('click', () => performReset(true));

// ── Helpers ───────────────────────────────────────────────────────────────

/**
 * @param {HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement} input
 * @param {unknown} value
 * @param {string} type
 * @returns {void}
 */
function setInputValue(input, value, type) {
    if (type === 'bool') {
        if (input instanceof HTMLInputElement) input.checked = !!value;
    } else if (input.tagName === 'SELECT') {
        input.value = String(value ?? '');
    } else if (type === 'password') {
        input.value = '';
        if (input instanceof HTMLInputElement) input.placeholder = value ? '(set — enter to change)' : '(not set)';
    } else if (type === 'prompt-template') {
        // textarea — sync backdrop too
        input.value = (value !== null && value !== undefined) ? String(value) : '';
        const backdrop = input.previousElementSibling;
        if (backdrop?.classList.contains('prompt-backdrop')) {
            backdrop.innerHTML = escHtml(input.value).replace(/\{(\w+)\}/g, '<mark>{$1}</mark>');
        }
    } else {
        input.value = (value !== null && value !== undefined) ? String(value) : '';
    }
}

/**
 * @param {HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement} input
 * @param {string} type
 * @returns {string | number | boolean | null}
 */
function getInputValue(input, type) {
    if (type === 'bool')  return input instanceof HTMLInputElement ? input.checked : false;
    if (type === 'int')   return input.value === '' ? null : parseInt(input.value, 10);
    if (type === 'float') return input.value === '' ? null : parseFloat(input.value);
    const v = input.value.trim();
    return v === '' ? null : v;
}

/**
 * @param {string} sectionKey
 * @param {string} fieldKey
 * @returns {HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | null}
 */
function findInput(sectionKey, fieldKey) {
    return document.querySelector(
        `input[data-section="${sectionKey}"][data-field="${fieldKey}"],` +
        `select[data-section="${sectionKey}"][data-field="${fieldKey}"],` +
        `textarea[data-section="${sectionKey}"][data-field="${fieldKey}"]`
    );
}

/**
 * @param {string} sectionKey
 * @param {string} fieldKey
 * @returns {ConfigField | undefined}
 */
function findFieldMeta(sectionKey, fieldKey) {
    const section = serverDefaults.find(s => s.key === sectionKey);
    return section?.fields.find(f => f.key === fieldKey);
}

/**
 * @param {string} sectionKey
 * @param {string} fieldKey
 * @param {boolean} on
 * @returns {void}
 */
function markBrowserOverride(sectionKey, fieldKey, on) {
    const row = document.querySelector(
        `.field-row[data-section="${sectionKey}"][data-field="${fieldKey}"]`
    );
    if (!row) return;
    const badge = /** @type {HTMLElement | null} */ (row.querySelector('.field-browser-badge'));
    if (badge) badge.style.display = on ? 'inline' : 'none';
}

/**
 * @returns {SectionValueMap}
 */
function loadFromStorage() {
    try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}'); }
    catch { return {}; }
}

/**
 * @param {SectionValueMap} data
 * @returns {void}
 */
function persistToStorage(data) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
}

/**
 * @param {string} msg
 * @returns {void}
 */
function setStatus(msg) {
    const el = document.getElementById('save-status');
    if (el) el.textContent = msg;
}

/** @type {ReturnType<typeof setTimeout> | undefined} */
let toastTimer;
/**
 * @param {string} msg
 * @param {boolean} [isError]
 * @returns {void}
 */
function showToast(msg, isError = false) {
    const t = document.getElementById('toast');
    if (!t) return;
    t.textContent = msg;
    t.classList.toggle('error', isError);
    t.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove('show'), isError ? 3500 : 2000);
}

/**
 * @param {string} str
 * @returns {string}
 */
function escHtml(str) {
    const d = document.createElement('div');
    d.textContent = str;
    return d.innerHTML;
}

export {};

init();