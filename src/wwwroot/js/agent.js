// @ts-check
// ── Type definitions ──────────────────────────────────────────────────────

/** @typedef {{ aiResponseMs: number, parseMs: number, executionMs: number, coordResolutionMs?: number }} StepTimings */
/** @typedef {{ label: string, text?: string, imageBase64?: string }} DebugLogEntry */
/** @typedef {{ action: string, result: string, durationMs: number, success: boolean, debugLog?: DebugLogEntry[] }} QueuedSubStep */
/** @typedef {{ type: 'step', stepNumber: number, thought: string, action: string, result: string, durationMs: number, success: boolean, isTerminal?: boolean, goalAchieved?: boolean, isParseRejected?: boolean, timings?: StepTimings, debugLog?: DebugLogEntry[], queuedSubSteps?: QueuedSubStep[] }} AgentStepMessage */
/** @typedef {{ type: 'stepStarting', stepNumber: number, thought: string, action: string, queueSize: number }} AgentStepStartingMessage */
/** @typedef {{ type: 'subStep', label: string, text?: string, imageBase64?: string }} AgentSubStepMessage */
/** @typedef {{ type: 'countdown', seconds: number }} AgentCountdownMessage */
/** @typedef {{ type: 'guidanceQueued', message: string, cancelNextAction: boolean }} AgentGuidanceQueuedMessage */
/** @typedef {{ type: 'done', status: string, finalResult?: string, totalDurationMs?: number }} AgentDoneMessage */
/** @typedef {AgentStepMessage | AgentStepStartingMessage | AgentSubStepMessage | AgentCountdownMessage | AgentGuidanceQueuedMessage | AgentDoneMessage | { type: 'paused' } | { type: 'resumed' }} AgentSseMessage */
/** @typedef {{ general?: { activeProvider?: string }, gemini?: { model?: string, apiKey?: string }, openai?: { model?: string, apiKey?: string }, anthropic?: { model?: string, apiKey?: string }, [key: string]: (Record<string, unknown> | undefined) }} StoredConfigData */
/** @typedef {{ index: number, isPrimary: boolean, width: number, height: number }} MonitorInfo */
/** @typedef {{ goal?: string, status: string, isPaused: boolean, startedAt?: string, totalDurationMs?: number, finalResult?: string }} SessionStatusResponse */
/** @typedef {{ sessionId: string }} StartAgentResponse */
/** @typedef {{ label: string, text?: string, image?: string }} CopyDebugEntry */
/** @typedef {{ stepNumber: number, thought: string, action: string, result: string, durationMs: number, timings?: StepTimings, isTerminal?: true, goalAchieved?: boolean, debugLog?: CopyDebugEntry[] }} CopyOutputStep */
/** @typedef {{ goal: string, status: string, finalResult?: string, steps: CopyOutputStep[] }} CopyOutput */

const monitorSelect  =  /** @type {HTMLSelectElement} */  (document.getElementById('monitor-select'));
const goalInput       = /** @type {HTMLInputElement} */   (document.getElementById('goal-input'));
const btnStart        = /** @type {HTMLButtonElement} */  (document.getElementById('btn-start'));
const countdownBanner = /** @type {HTMLDivElement} */     (document.getElementById('countdown-banner'));
const countdownNum    = /** @type {HTMLSpanElement} */    (document.getElementById('countdown-num'));
const btnPause        = /** @type {HTMLButtonElement} */  (document.getElementById('btn-pause'));
const btnStop         = /** @type {HTMLButtonElement} */  (document.getElementById('btn-stop'));
const btnNewSession   = /** @type {HTMLButtonElement} */  (document.getElementById('btn-new-session'));
const statusDot       = /** @type {HTMLDivElement} */     (document.getElementById('status-dot'));
const statusText      = /** @type {HTMLSpanElement} */    (document.getElementById('status-text'));
const stepCounter     = /** @type {HTMLSpanElement} */    (document.getElementById('step-counter'));
const finalResultEl   = /** @type {HTMLDivElement} */     (document.getElementById('final-result'));
const finalResultMsg  = /** @type {HTMLSpanElement} */    (document.getElementById('final-result-msg'));
const finalResultTime = /** @type {HTMLSpanElement} */    (document.getElementById('final-result-time'));
const currentThought  = /** @type {HTMLDivElement} */     (document.getElementById('current-thought'));
const currentAction   = /** @type {HTMLDivElement} */     (document.getElementById('current-action'));
const currentResult   = /** @type {HTMLDivElement} */     (document.getElementById('current-result'));
const liveSubsteps    = /** @type {HTMLDivElement} */     (document.getElementById('live-substeps'));
const stepLog         = /** @type {HTMLDivElement} */     (document.getElementById('step-log'));
const promptBanner    = /** @type {HTMLDivElement} */     (document.getElementById('prompt-banner'));
const promptPreview   = /** @type {HTMLSpanElement} */    (document.getElementById('prompt-preview'));
const promptFull      = /** @type {HTMLDivElement} */     (document.getElementById('prompt-full'));
const elapsedTimer    = /** @type {HTMLSpanElement} */    (document.getElementById('elapsed-timer'));
const modelBadge      = /** @type {HTMLSpanElement} */    (document.getElementById('model-badge'));
const guidancePanel   = /** @type {HTMLDivElement} */     (document.getElementById('guidance-panel'));
const guidanceInput   = /** @type {HTMLInputElement} */   (document.getElementById('guidance-input'));
const btnGuidance     = /** @type {HTMLButtonElement} */  (document.getElementById('btn-guidance'));
const guidanceAck     = /** @type {HTMLSpanElement} */    (document.getElementById('guidance-ack'));

const SESSION_STORE_KEY = 'tua_session_id';
const CONFIG_STORE_KEY  = 'tua_config_v1';

/**
 * @returns {StoredConfigData}
 */
function getStoredConfig() {
    try { return JSON.parse(localStorage.getItem(CONFIG_STORE_KEY) || '{}'); } catch { return {}; }
}

/** @returns {Promise<void>} */
async function pushConfigToServer() {
    const cfg = getStoredConfig();
    if (!cfg || Object.keys(cfg).length === 0) return;
    try {
        await fetch('/api/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(cfg),
        });
    } catch { /* best effort */ }
}

/** @type {string | null} */
let sessionId = null;
/** @type {EventSource | null} */
let eventSource = null;
let debugEnabled = false;
/** @type {AgentStepMessage[]} */
let stepsData = [];
let sessionGoal = '';
let sessionFinalResult = '';
let isPaused = false;
/** @type {number | null} */
let sessionStartTime = null;
/** @type {ReturnType<typeof setInterval> | undefined} */
let elapsedIntervalId;

// ── Boot: fetch server config + try to resume a stored session ──────────
(async () => {
    try {
        const [cfgRes, dbgRes, monRes] = await Promise.all([
            fetch('/api/config'),
            fetch('/api/agent/debug-enabled'),
            fetch('/api/agent/monitors'),
        ]);

        if (cfgRes.ok) {
            const cfg = await cfgRes.json();
            const stored = getStoredConfig();
            const activeProvider = stored?.general?.activeProvider || cfg.general?.activeProvider || 'Gemini';
            const providerKeyMap = /** @type {Record<string, string>} */({ 'Gemini': 'gemini', 'ChatGPT': 'openai', 'Claude': 'anthropic' });
            const providerKey = providerKeyMap[activeProvider] ?? activeProvider.toLowerCase();
            const storedModel = stored?.[providerKey]?.model;
            const model = storedModel || cfg[providerKey]?.model || '';
            modelBadge.textContent = model ? `${activeProvider}: ${model}` : activeProvider;
        }

        if (dbgRes.ok) {
            const data = await dbgRes.json();
            debugEnabled = !!data.enabled;
            if (debugEnabled) statusText.textContent = 'Idle (Debug Mode)';
        }

        monitorSelect.innerHTML = '<option value="">All Monitors</option>';
        if (monRes.ok) {
            const monitors = await monRes.json();
            for (const m of monitors) {
                const opt = document.createElement('option');
                opt.value = m.index;
                opt.textContent = `Monitor ${m.index}${m.isPrimary ? ' (Primary)' : ''} — ${m.width}\u00d7${m.height}`;
                monitorSelect.appendChild(opt);
            }
        }
    } catch { /* non-critical */ }

    // ── Session recovery ────────────────────────────────────────────────
    const storedSid = localStorage.getItem(SESSION_STORE_KEY);
    if (storedSid) {
        try {
            const statusRes = await fetch(`/api/agent/${storedSid}/status`);
            if (!statusRes.ok) throw new Error('not found');
            const status = await statusRes.json();

            sessionId   = storedSid;
            sessionGoal = status.goal || '';

            goalInput.value = sessionGoal;
            promptPreview.textContent = sessionGoal.length > 80 ? sessionGoal.slice(0, 80) + '\u2026' : sessionGoal;
            promptFull.textContent = sessionGoal;
            promptBanner.style.display = '';

            const isActive = status.status === 'Running';
            const isPausedStatus = status.isPaused;

            if (isActive) {
                sessionStartTime = status.startedAt ? new Date(status.startedAt).getTime() : Date.now();
                clearInterval(elapsedIntervalId);
                elapsedIntervalId = setInterval(() => {
                    elapsedTimer.textContent = formatElapsed(Date.now() - (sessionStartTime ?? 0));
                }, 500);

                setStatus('running', debugEnabled ? 'Running (Debug Mode)' : 'Running');
                btnStart.disabled  = true;
                btnPause.disabled  = false;
                btnStop.disabled   = false;
                goalInput.disabled = true;
                monitorSelect.disabled = true;
                guidancePanel.style.display = 'flex';
                guidanceInput.value = '';

                if (isPausedStatus) {
                    isPaused = true;
                    btnPause.textContent = 'Resume';
                    btnPause.classList.add('resuming');
                    setStatus('paused', debugEnabled ? 'Paused (Debug Mode)' : 'Paused');
                }
            } else {
                // Show the final result immediately from the status response so it
                // appears before the SSE step history finishes replaying.
                const sk = status.status.toLowerCase();
                if (status.totalDurationMs != null) sessionStartTime = Date.now() - status.totalDurationMs;
                setStatus(sk, status.status);
                if (status.finalResult) showFinalResult(sk, status.finalResult);
                resetControls();
            }

            // Connect SSE: for running sessions this streams live events; for
            // terminated sessions it replays all steps and fires the done event.
            connectStream(storedSid);
        } catch {
            // Session expired (server restarted or id stale) — silently clear
            localStorage.removeItem(SESSION_STORE_KEY);
        }
    }
})();

goalInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !btnStart.disabled) startAgent();
});

btnStart.addEventListener('click', startAgent);
btnPause.addEventListener('click', togglePause);
btnStop.addEventListener('click', stopAgent);
btnNewSession.addEventListener('click', newSession);
document.getElementById('btn-copy-steps')?.addEventListener('click', copySteps);
btnGuidance.addEventListener('click', sendGuidance);
guidanceInput.addEventListener('keydown', e => { if (e.key === 'Enter') sendGuidance(); });
document.getElementById('btn-skip-countdown')?.addEventListener('click', skipCountdown);

/** @returns {Promise<void>} */
async function startAgent() {
    const goal = goalInput.value.trim();
    if (!goal) return;

    await pushConfigToServer();

    const cfg = getStoredConfig();
    const activeProvider = cfg?.general?.activeProvider || 'Gemini';
    const providerKeyMap = /** @type {Record<string, string>} */({ 'Gemini': 'gemini', 'ChatGPT': 'openai', 'Claude': 'anthropic' });
    const providerKey = providerKeyMap[activeProvider] ?? activeProvider.toLowerCase();
    const providerCfg = /** @type {{ model?: string, apiKey?: string } | undefined} */ (cfg?.[providerKey]);
    const apiKey = providerCfg?.apiKey || null;
    const model  = providerCfg?.model  || null;

    // Clear any previous session from storage so a fresh session takes over
    localStorage.removeItem(SESSION_STORE_KEY);

    // Reset UI
    stepsData = [];
    sessionGoal = goal;
    sessionFinalResult = '';
    isPaused = false;
    sessionStartTime = Date.now();
    elapsedTimer.textContent = '0:00';
    clearInterval(elapsedIntervalId);
    elapsedIntervalId = setInterval(() => {
        elapsedTimer.textContent = formatElapsed(Date.now() - (sessionStartTime ?? 0));
    }, 500);
    stepLog.innerHTML = '';
    promptPreview.textContent = goal.length > 80 ? goal.slice(0, 80) + '…' : goal;
    promptFull.textContent = goal;
    promptBanner.style.display = '';
    document.getElementById('prompt-details')?.removeAttribute('open');
    finalResultEl.className = 'final-result';
    finalResultMsg.textContent = '';
    finalResultTime.textContent = '';
    finalResultEl.style.display = 'none';
    currentThought.textContent = 'Starting...';
    currentAction.style.display = 'none';
    currentAction.textContent = '';
    currentResult.textContent = '';
    liveSubsteps.innerHTML = '';
    stepCounter.textContent = '';
    setStatus('running', debugEnabled ? 'Running (Debug Mode)' : 'Running');

    btnStart.disabled = true;
    btnPause.disabled = false;
    btnPause.textContent = 'Pause';
    btnPause.classList.remove('resuming');
    btnStop.disabled = false;
    goalInput.disabled = true;
    monitorSelect.disabled = true;
    guidancePanel.style.display = 'flex';
    guidanceInput.value = '';

    try {
        const res = await fetch('/api/agent/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                goal,
                apiKey,
                model,
                monitorIndex: monitorSelect.value !== '' ? parseInt(monitorSelect.value, 10) : null,
            }),
        });

        if (!res.ok) {
            const err = await res.json();
            showFinalResult('error', err.error || 'Failed to start agent.');
            resetControls();
            return;
        }

        const data = /** @type {StartAgentResponse} */ (await res.json());
        sessionId = data.sessionId;
        localStorage.setItem(SESSION_STORE_KEY, sessionId);

        connectStream(sessionId);
    } catch (err) {
        showFinalResult('error', 'Network error: ' + (err instanceof Error ? err.message : String(err)));
        resetControls();
    }
}

/**
 * @param {string} id Session ID to connect to
 * @returns {void}
 */
function connectStream(id) {
    if (eventSource) { eventSource.close(); eventSource = null; }
    eventSource = new EventSource(`/api/agent/${id}/stream`);
    eventSource.onmessage = (e) => {
        const msg = /** @type {AgentSseMessage} */ (JSON.parse(e.data));
        if (msg.type === 'stepStarting') handleStepStarting(msg);
        else if (msg.type === 'subStep') handleSubStep(msg);
        else if (msg.type === 'step') handleStep(msg);
        else if (msg.type === 'paused') handlePaused();
        else if (msg.type === 'resumed') handleResumed();
        else if (msg.type === 'countdown') handleCountdown(msg);
        else if (msg.type === 'guidanceQueued') handleGuidanceQueued(msg, msg.cancelNextAction);
        else if (msg.type === 'done') handleDone(msg);
    };
    eventSource.onerror = () => {
        eventSource?.close();
        eventSource = null;
        if (statusText.textContent?.startsWith('Running')) {
            setStatus('error', 'Connection lost');
            resetControls();
        }
    };
}

/** @returns {Promise<void>} */
async function stopAgent() {
    if (!sessionId) return;
    btnStop.disabled = true;
    try {
        await fetch(`/api/agent/${sessionId}/stop`, { method: 'POST' });
    } catch { /* best effort */ }
}

/** @returns {Promise<void>} */
async function skipCountdown() {
    if (!sessionId) return;
    try {
        await fetch(`/api/agent/${sessionId}/skip-countdown`, { method: 'POST' });
    } catch { /* best effort */ }
}

/** @returns {Promise<void>} */
async function newSession() {
    // Cancel any in-progress session first (best effort)
    if (sessionId) {
        try { await fetch(`/api/agent/${sessionId}/stop`, { method: 'POST' }); } catch { /* best effort */ }
    }

    // Drop the SSE connection and clear persisted session key
    if (eventSource) { eventSource.close(); eventSource = null; }
    localStorage.removeItem(SESSION_STORE_KEY);
    sessionId = null;
    sessionGoal = '';
    sessionFinalResult = '';
    isPaused = false;
    stepsData = [];
    stopElapsedTimer(null);
    elapsedTimer.textContent = '';

    // Reset all UI
    stepLog.innerHTML = '';
    promptBanner.style.display = 'none';
    countdownBanner.style.display = 'none';
    finalResultEl.className = 'final-result';
    finalResultEl.style.display = 'none';
    finalResultMsg.textContent = '';
    finalResultTime.textContent = '';
    currentThought.textContent = 'Waiting for agent to start...';
    currentAction.style.display = 'none';
    currentAction.textContent = '';
    currentResult.textContent = '';
    liveSubsteps.innerHTML = '';
    stepCounter.textContent = '';
    goalInput.value = '';
    setStatus('idle', debugEnabled ? 'Idle (Debug Mode)' : 'Idle');
    resetControls();
}

/** @returns {Promise<void>} */
async function togglePause() {
    if (!sessionId) return;
    btnPause.disabled = true;
    try {
        if (isPaused) {
            await pushConfigToServer();
            await fetch(`/api/agent/${sessionId}/resume`, { method: 'POST' });
        } else {
            await fetch(`/api/agent/${sessionId}/pause`, { method: 'POST' });
        }
    } catch { /* best effort */ } finally {
        btnPause.disabled = false;
    }
}

/** @returns {Promise<void>} */
async function sendGuidance() {
    if (!sessionId) return;
    const msg = guidanceInput.value.trim();
    if (!msg) return;
    const cancelNext = /** @type {HTMLInputElement | null} */ (document.getElementById('guidance-cancel'))?.checked ?? false;
    btnGuidance.disabled = true;
    try {
        const res = await fetch(`/api/agent/${sessionId}/guidance`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ message: msg, cancelNextAction: cancelNext }),
        });
        if (res.ok) {
            guidanceInput.value = '';
            const cancelEl = /** @type {HTMLInputElement | null} */ (document.getElementById('guidance-cancel'));
            if (cancelEl) cancelEl.checked = false;
            // The SSE guidanceQueued event will fire shortly and add the log entry + ack badge.
        } else {
            const body = await res.json().catch(() => ({}));
            guidanceAck.textContent = '\u26a0 ' + (body.error ?? 'Failed to send');
            guidanceAck.style.color = '#cc6644';
            guidanceAck.classList.add('show');
            setTimeout(() => { guidanceAck.classList.remove('show'); guidanceAck.style.color = ''; guidanceAck.textContent = '\u2713 Queued'; }, 3000);
        }
    } catch { /* network error — silently ignore */ } finally {
        btnGuidance.disabled = false;
    }
}

/**
 * @param {AgentGuidanceQueuedMessage | string} msg
 * @param {boolean} cancelNext
 * @returns {void}
 */
function handleGuidanceQueued(msg, cancelNext) {
    // msg may be the raw SSE payload object {type, message} or undefined when called directly from sendGuidance
    const text = (typeof msg === 'object' && msg.message) ? msg.message : (typeof msg === 'string' ? msg : '');
    const isCancel = cancelNext ?? false;

    // Insert a visual entry into the step log
    const entry = document.createElement('div');
    entry.className = 'step-entry guidance-entry' + (isCancel ? ' cancel-flag' : '');
    entry.innerHTML = `<span class="guidance-label">${isCancel ? '\u26a0\ufe0e Guidance (cancel next)' : '\ud83d\udcac Guidance'}</span><span class="guidance-text">${escapeHtml(text)}</span>`;
    stepLog.appendChild(entry);
    stepLog.scrollTop = stepLog.scrollHeight;

    // Flash the ack badge (only meaningful when called from the local sendGuidance path)
    guidanceAck.textContent = isCancel ? '\u26a0 Queued (will cancel next action)' : '\u2713 Queued';
    guidanceAck.style.color = isCancel ? '#cc9944' : '';
    guidanceAck.classList.remove('show');
    void guidanceAck.offsetWidth;
    guidanceAck.classList.add('show');
    setTimeout(() => { guidanceAck.classList.remove('show'); guidanceAck.style.color = ''; guidanceAck.textContent = '\u2713 Queued'; }, 2500);
}

/** @returns {void} */
function handlePaused() {
    isPaused = true;
    btnPause.textContent = 'Resume';
    btnPause.classList.add('resuming');
    setStatus('paused', debugEnabled ? 'Paused (Debug Mode)' : 'Paused');
}

/** @returns {void} */
function handleResumed() {
    isPaused = false;
    btnPause.textContent = 'Pause';
    btnPause.classList.remove('resuming');
    setStatus('running', debugEnabled ? 'Running (Debug Mode)' : 'Running');
    // Countdown will appear via countdown events; clear any leftover banner just in case.
    countdownBanner.style.display = 'none';
}

/**
 * @param {AgentCountdownMessage} msg
 * @returns {void}
 */
function handleCountdown(msg) {
    if (msg.seconds > 0) {
        countdownNum.textContent = String(msg.seconds);
        countdownBanner.style.display = 'flex';
    } else {
        countdownBanner.style.display = 'none';
    }
}

/**
 * @param {AgentStepStartingMessage} msg
 * @returns {void}
 */
function handleStepStarting(msg) {
    stepCounter.textContent = `Step ${msg.stepNumber}`;

    currentThought.textContent = msg.thought;
    const queueBadge = (msg.queueSize > 1) ? ` <span class="queue-badge">QUEUE ×${msg.queueSize}</span>` : '';
    currentAction.innerHTML = escapeHtml(msg.action) + queueBadge;
    currentAction.style.display = 'inline-block';
    currentResult.textContent = 'Executing…';
    liveSubsteps.innerHTML = '';
}

/**
 * @param {AgentSubStepMessage} msg
 * @returns {void}
 */
function handleSubStep(msg) {
    if (!debugEnabled && !msg.imageBase64) return;

    const entry = document.createElement('div');
    entry.className = 'debug-entry';

    let html = `<div class="debug-entry-label">${escapeHtml(msg.label)}</div>`;
    if (msg.text) {
        html += `<div class="debug-entry-text">${escapeHtml(msg.text)}</div>`;
    }
    if (msg.imageBase64) {
        html += `<img class="debug-entry-img" src="data:image/png;base64,${msg.imageBase64}" alt="${escapeHtml(msg.label)}" title="Click to expand" />`;
    }

    entry.innerHTML = html;
    entry.querySelectorAll('.debug-entry-img').forEach(img => {
        img.addEventListener('click', () => img.classList.toggle('expanded'));
    });

    // Show a header if this is the first sub-step
    if (liveSubsteps.children.length === 0) {
        const header = document.createElement('div');
        header.className = 'live-substep-header';
        header.textContent = 'Live Progress';
        liveSubsteps.appendChild(header);
    }

    liveSubsteps.appendChild(entry);
    liveSubsteps.scrollTop = liveSubsteps.scrollHeight;
}

/**
 * @param {AgentStepMessage} msg
 * @returns {void}
 */
function handleStep(msg) {
    stepsData.push(msg);
    stepCounter.textContent = `Step ${msg.stepNumber}`;

    liveSubsteps.innerHTML = '';

    currentThought.textContent = msg.thought;
    const queueBadge = (msg.queuedSubSteps && msg.queuedSubSteps.length > 1)
        ? ` <span class="queue-badge">QUEUE ×${msg.queuedSubSteps.length}</span>` : '';
    currentAction.innerHTML = escapeHtml(msg.action) + queueBadge;
    currentAction.style.display = 'inline-block';
    currentResult.textContent = msg.result;

    const entry = document.createElement('div');
    const isCancelled = !msg.success && !msg.isTerminal && msg.result === 'Action cancelled by user.';
    const isParseRejected = !!msg.isParseRejected;
    entry.className = 'step-entry' + (isCancelled ? ' cancelled' : '') + (isParseRejected ? ' parse-rejected' : '');

    let html = `
        <span class="step-num">#${msg.stepNumber}</span>
        <span class="step-action">${escapeHtml(msg.action)}${queueBadge}</span>
        <span class="step-duration">${formatDuration(msg.durationMs)}</span>
        <div class="step-thought">${escapeHtml(msg.thought)}</div>
        <div class="step-result">${escapeHtml(msg.result)}</div>
    `;

    if (msg.timings) {
        const t = msg.timings;
        let timingsHtml = '<div class="step-timings">';
        timingsHtml += `<span class="timing-pill timing-ai" title="Time for AI to respond">AI&nbsp;${formatDuration(t.aiResponseMs)}</span>`;
        timingsHtml += `<span class="timing-pill timing-parse" title="Time to parse AI response">Parse&nbsp;${formatDuration(t.parseMs)}</span>`;
        timingsHtml += `<span class="timing-pill timing-exec" title="Time to execute action">Exec&nbsp;${formatDuration(t.executionMs)}</span>`;
        if (t.coordResolutionMs != null)
            timingsHtml += `<span class="timing-pill timing-coord" title="Coord resolution (subset of Exec)">Coord&nbsp;${formatDuration(t.coordResolutionMs)}</span>`;
        timingsHtml += '</div>';
        html += timingsHtml;
    }

    // Nested expandable queue sub-steps
    if (msg.queuedSubSteps && msg.queuedSubSteps.length > 1) {
        html += renderQueuedSubSteps(msg.queuedSubSteps);
    }

    if (msg.debugLog && msg.debugLog.length > 0) {
        const visibleEntries = debugEnabled
            ? msg.debugLog
            : msg.debugLog.filter(e => e.imageBase64);
        if (visibleEntries.length > 0) {
            html += renderDebugLog(visibleEntries);
        }
    }

    entry.innerHTML = html;

    entry.querySelectorAll('.debug-entry-img').forEach(img => {
        img.addEventListener('click', () => img.classList.toggle('expanded'));
    });

    stepLog.appendChild(entry);
    stepLog.scrollTop = stepLog.scrollHeight;

    if (msg.isTerminal) {
        const status = msg.goalAchieved ? 'completed' : 'failed';
        sessionFinalResult = msg.result;
        setStatus(status, msg.goalAchieved ? 'Completed' : 'Failed');
        showFinalResult(status, msg.result);
        stopElapsedTimer(null);
        cleanup();
    }
}

/**
 * @param {QueuedSubStep[]} subSteps
 * @returns {string} HTML string
 */
function renderQueuedSubSteps(subSteps) {
    let html = `<details class="queue-substeps"><summary>Queued Actions <span class="queue-badge">${subSteps.length} actions</span></summary><div class="queue-substep-list">`;
    for (const s of subSteps) {
        const ok = s.success;
        html += `<div class="queue-substep-item">`;
        html += `<div class="queue-substep-action">${escapeHtml(s.action)}<span class="step-duration">&nbsp;${formatDuration(s.durationMs)}</span></div>`;
        html += `<div class="queue-substep-result">${ok ? '✓' : '✗'}&nbsp;${escapeHtml(s.result)}</div>`;
        if (s.debugLog && debugEnabled) {
            const vis = s.debugLog.filter(e => e.text || e.imageBase64);
            if (vis.length > 0) html += renderDebugLog(vis);
        }
        html += `</div>`;
    }
    html += `</div></details>`;
    return html;
}

/**
 * @param {DebugLogEntry[]} entries
 * @returns {string} HTML string
 */
function renderDebugLog(entries) {
    let html = `<details class="debug-toggle"><summary>Debug Info <span class="debug-badge">${entries.length} entries</span></summary><div class="debug-entries">`;

    for (const e of entries) {
        html += `<div class="debug-entry">`;
        html += `<div class="debug-entry-label">${escapeHtml(e.label)}</div>`;

        if (e.text) {
            html += `<div class="debug-entry-text">${escapeHtml(e.text)}</div>`;
        }

        if (e.imageBase64) {
            html += `<img class="debug-entry-img" src="data:image/png;base64,${e.imageBase64}" alt="${escapeHtml(e.label)}" title="Click to expand" />`;
        }

        html += `</div>`;
    }

    html += `</div></details>`;
    return html;
}

/**
 * @param {AgentDoneMessage} msg
 * @returns {void}
 */
function handleDone(msg) {
    const statusKey = msg.status.toLowerCase();
    if (msg.finalResult) sessionFinalResult = msg.finalResult;
    setStatus(statusKey, msg.status);
    if (msg.finalResult) showFinalResult(statusKey, msg.finalResult);
    stopElapsedTimer(msg.totalDurationMs);
    localStorage.removeItem(SESSION_STORE_KEY);
    cleanup();
}

/** @returns {Promise<void>} */
async function copySteps() {
    if (stepsData.length === 0) return;
    const btn = /** @type {HTMLButtonElement} */ (document.getElementById('btn-copy-steps'));

    /** @type {CopyOutput} */
    const output = {
        goal: sessionGoal,
        status: statusText.textContent ?? '',
        ...(sessionFinalResult ? { finalResult: sessionFinalResult } : {}),
        steps: stepsData.map(s => {
            const step = /** @type {CopyOutputStep} */ ({
                stepNumber: s.stepNumber,
                thought: s.thought,
                action: s.action,
                result: s.result,
                durationMs: s.durationMs,
            });
            if (s.timings) step.timings = s.timings;
            if (s.isTerminal) step.isTerminal = true;
            if (s.goalAchieved != null) step.goalAchieved = s.goalAchieved;
            if (s.debugLog?.length) {
                step.debugLog = s.debugLog.map(d => {
                    const entry = /** @type {CopyDebugEntry} */ ({ label: d.label });
                    if (d.text) entry.text = d.text;
                    if (d.imageBase64) entry.image = '[image data omitted]';
                    return entry;
                });
            }
            return step;
        }),
    };

    const json = JSON.stringify(output, null, 2);
    try {
        await navigator.clipboard.writeText(json);
    } catch {
        const ta = document.createElement('textarea');
        ta.value = json;
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
    }
    btn.textContent = 'Copied!';
    btn.classList.add('copied');
    setTimeout(() => { btn.textContent = 'Copy'; btn.classList.remove('copied'); }, 1500);
}

/**
 * @param {number | null | undefined} totalDurationMs
 * @returns {void}
 */
function stopElapsedTimer(totalDurationMs) {
    clearInterval(elapsedIntervalId);
    elapsedIntervalId = undefined;
    const finalMs = totalDurationMs != null ? totalDurationMs
        : sessionStartTime ? Date.now() - sessionStartTime : null;
    if (finalMs != null) {
        elapsedTimer.textContent = formatElapsed(finalMs);
        finalResultTime.textContent = formatElapsed(finalMs);
    }
}

/**
 * @param {number} ms
 * @returns {string}
 */
function formatElapsed(ms) {
    if (ms == null) return '';
    const totalSec = Math.floor(ms / 1000);
    const h = Math.floor(totalSec / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    if (h > 0) return `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;
    return `${m}:${String(s).padStart(2,'0')}`;
}

/**
 * @param {number | null | undefined} ms
 * @returns {string}
 */
function formatDuration(ms) {
    if (ms == null) return '';
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(1)}s`;
}

/**
 * @param {string} key CSS class key for the status dot
 * @param {string} label Display text
 * @returns {void}
 */
function setStatus(key, label) {
    statusDot.className = 'status-dot ' + key;
    statusText.textContent = label;
}

/**
 * @param {string} statusKey CSS class suffix
 * @param {string} message Result text to display
 * @returns {void}
 */
function showFinalResult(statusKey, message) {
    finalResultEl.className = 'final-result ' + statusKey;
    finalResultMsg.textContent = message;
    finalResultTime.textContent = '';
    finalResultEl.style.display = 'flex';
}

/** @returns {void} */
function cleanup() {
    if (eventSource) {
        eventSource.close();
        eventSource = null;
    }
    countdownBanner.style.display = 'none';
    resetControls();
}

/** @returns {void} */
function resetControls() {
    btnStart.disabled = false;
    btnPause.disabled = true;
    btnPause.textContent = 'Pause';
    btnPause.classList.remove('resuming');
    btnStop.disabled = true;
    goalInput.disabled = false;
    monitorSelect.disabled = false;
    guidancePanel.style.display = 'none';
    guidanceAck.classList.remove('show');
}

/**
 * @param {string} str
 * @returns {string}
 */
function escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

export {};
