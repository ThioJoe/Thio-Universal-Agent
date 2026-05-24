// @ts-check
// ── Type definitions ──────────────────────────────────────────────────────

/** @typedef {{ key: string, label: string, type: string, value: unknown, nullable?: boolean, options?: string[], description?: string, defaultTemplate?: string }} ConfigField */
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

const STORAGE_KEY  = 'tua_config_v1';
const VAULT_KEY    = 'tua_vault_hash_v1'; // stored only when "remember" is checked

/** @type {ConfigSection[]} */
let serverDefaults = []; // raw schema sections from /api/config/schema
/** @type {SectionValueMap} */
let browserValues  = {}; // persisted overrides { sectionKey: { fieldKey: value } }
/** @type {SectionValueMap} */
let pendingChanges = {}; // unsaved UI edits { sectionKey: { fieldKey: value } }

/** @type {string | null} */
let vaultPasswordHash  = null;  // SHA-256 hex provided by the browser this page visit
let vaultSessionActive = false; // true when server already has the hash from a prior unlock

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
 * Renders the vault section and inserts it at the top of #config-main.
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

    const main = document.getElementById('config-main');
    if (!main) return;
    main.insertBefore(wrap, main.firstChild);
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

    try {
        const res = await fetch('/api/config/schema');
        if (!res.ok) throw new Error('Schema fetch failed');
        const { sections } = await res.json();
        serverDefaults = sections;
        renderSections(sections);
        renderVaultSection();
        applyStoredValues();

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
 * @returns {void}
 */
function renderSections(sections) {
    const main = document.getElementById('config-main');
    if (!main) return;
    main.innerHTML = '';

    for (const section of sections) {
        const wrap = document.createElement('div');
        wrap.className = 'config-section';
        wrap.dataset.section = section.key;

        // Header
        const hdr = document.createElement('div');
        hdr.className = 'section-header';
        hdr.innerHTML = `<span class="section-title">${escHtml(section.label)}</span>`;
        if (section.isProvider)
            hdr.innerHTML += `<span class="provider-badge">AI Provider</span>`;
        wrap.appendChild(hdr);

        // Fields grid
        const grid = document.createElement('div');
        grid.className = 'fields-grid';

        for (const field of section.fields) {
            const row = buildFieldRow(section.key, field);
            grid.appendChild(row);
        }

        wrap.appendChild(grid);
        main.appendChild(wrap);
    }
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

    if (field.type === 'bool') {
        input = document.createElement('input');
        input.type = 'checkbox';
    } else if (field.type === 'enum') {
        input = document.createElement('select');
        if (field.nullable) {
            const opt = document.createElement('option');
            opt.value = '';
            opt.textContent = '— server default —';
            input.appendChild(opt);
        }
        for (const opt of (field.options || [])) {
            const el = document.createElement('option');
            el.value = opt;
            el.textContent = opt;
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