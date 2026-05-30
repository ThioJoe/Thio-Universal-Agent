# AI Context: Thio Universal Agent (TUA)

## 1. Project Overview
**Thio Universal Agent** is an autonomous, cross-platform AI desktop assistant capable of host-native execution. It uses a Vision-Language Model to visually "see" the computer screen and perform tasks by taking physical control of the mouse and keyboard.

**Tech Stack:**
* **Backend:** C# / .NET 10, ASP.NET Core Minimal APIs.
* **Frontend:** Vanilla HTML/CSS/JS (embedded in the assembly via `wwwroot`). No modern JS frameworks.
* **OS Integration:** Windows (implemented via P/Invoke `user32.dll`, `gdi32.dll`), architected via interfaces for future cross-platform support.
* **AI Provider:** Currently OpenAI ChatGPT, OpenAI-compatible chat-completions APIs (with a user-defined endpoint URL and optional API key for local/self-hosted services), Google Gemini, Anthropic Claude, and local ONNX Runtime GenAI models, using structured text/vision prompting.

---

## 2. Core Architecture & Data Flow
The system operates on an **Observe-Think-Act** loop, managed asynchronously and entirely decoupled from the OS using Dependency Injection.

1. **Observe:** `IScreenProvider` captures the desktop into a `Screenshot` object.
2. **Think:** `AgentLoop` sends the image and text prompts (built by `AgentPromptBuilder`) via `IAiProvider`.
3. **Parse:** `AgentActionParser` strictly extracts the tool intent (e.g., `LEFT_CLICK`, `TYPE_TEXT`, or a `QUEUE:` of actions).
4. **Resolve:** If a click/drag is required, `CoordinatePrompter` calculates exact pixels (either directly via `DirectAutoNormalize` or visually via a `Zoom` grid overlay).
5. **Act:** `AgentActionExecutor` translates the parsed intent into OS commands via `IInputProvider`.
6. **Report:** Real-time progress is streamed to the `wwwroot` frontend via Server-Sent Events (SSE) from `AgentSession`.

---

## 3. Directory & File Mapping

### (Root)
* `Program.cs`: Entry point. Sets up OS-specific Dependency Injection (DI) based on the runtime OS, initializes the `AppConfig` singleton, registers Minimal API routes, and serves embedded static files.
* `GlobalSuppressions.cs`: Assembly-level `SuppressMessage` attributes that suppress specific Roslyn/code-analysis warnings project-wide (e.g. `SYSLIB1054` for `DllImport` vs `LibraryImport`, and select IDE style rules).

### `/AI_API/` (Provider Implementations)
* `ChatGPT/OpenAIConfig.cs`: Configuration for the direct OpenAI provider (API key, model, temperature, max output tokens, and pricing metadata).
* `ChatGPT/OpenAICompatibleConfig.cs`: Configuration for OpenAI-compatible chat-completions services. Adds a user-editable `EndpointUrl`; `ApiKey` may be left blank for local or self-hosted services that do not require authentication.
* `ChatGPT/OpenAIProvider.cs`: Shared chat-completions client used by both the `ChatGPT` and `OpenAICompatible` provider selections. For the compatible path it posts to the configured endpoint URL and only sends bearer auth when an API key is present.
* `Gemini/` and `Claude/`: Provider-specific config and transport implementations for Google Gemini and Anthropic Claude.
* `Onnx/OnnxConfig.cs` and `Onnx/OnnxProvider.cs`: Local ONNX Runtime GenAI support. Loads a model directly from disk, optionally configures an execution provider such as `DML`/`CUDA`, and supports screenshot-driven multimodal prompting through `MultiModalProcessor` when the export is vision-capable.

### `/Interfaces/` (The Abstraction Layer)
* `IAiProvider.cs`: Contract for the LLM (SendPrompt, StartConversation, ContinueConversation with/without images).
* `IAiProviderConfig.cs`: Common contract for every AI provider's configuration block (`ProviderName`, `ApiKey`, `Model`, token pricing fields). Each concrete implementation (e.g. `GeminiConfig`) adds provider-specific settings.
* `IScreenProvider.cs`: Contract for capturing screenshots and enumerating monitors.
* `InputProviders.cs`: Contract (`IInputProvider`) for simulating OS events (mouse clicks, drags, typing text, keyboard combos).
* `ISystemProvider.cs`: Contract for retrieving OS details (name, build) to feed the AI context.
* `IHotkeyProvider.cs`: Contract (`IHotkeyProvider`) for registering and unregistering system-wide hotkeys. Also defines the `HotkeyModifiers` flags enum (`Alt`, `Control`, `Shift`, `Win`, `NoRepeat`).


### `/Handlers/` (Core Logic)
* `AgentSessionManager.cs`: Singleton that creates `AgentSession`s and spawns the `AgentLoop` on background tasks. Handles stop/pause/resume requests from the UI.
* `AgentLoop.cs`: The core execution engine. Manages the Observe-Think-Act loop, handles token bloat via context resets, and triggers parsing retries on failure.
* `AgentActionParser.cs`: Parses the AI's exact text output into strongly-typed `AgentAction` records. Handles both single `ACTION:` and batched `QUEUE:` formats.
* `AgentActionExecutor.cs`: Takes an `AgentAction`, routes it through the `CoordinatePrompter` (if spatial resolution is needed), and then executes it via `IInputProvider`.
* `CoordinatePrompter.cs`: Complex visual logic. Determines exactly *where* to click based on the AI's target description. Uses modes like `Zoom` (drawing a grid over the screen) or `DirectAutoNormalize` (translating 1000x1000 normalized coordinates back to physical pixels).
* `ScreenshotProcessing.cs` (part of `CoordinatePrompter`): Handles SkiaSharp image manipulation, drawing grids, crosshairs, and cropping for zoom modes.
* `AgentPromptBuilder.cs`: Constructs the system prompt teaching the AI its tools, rules, and formats. Supports two operating modes driven by `AppConfig.General.HumanControlOnlyMode`:
  * **Autonomous mode** (default): The AI directly controls the machine. Prompts use first-person action language ("you click", "you type") and WAIT accepts a duration in seconds.
  * **Human Control Only mode**: The AI directs a human operator who carries out each action. All mode-dependent strings are stored in the nested `ModeStrings` static class (sub-classes: `Intro`, `Wait`, `ToolsContextNote`, `ReasonDescription`, `CoordsRule`, `VisualConfirmRule`, `CoordsPreferenceRule`). In this mode: the tools section opens with a note that the AI is directing a human; REASON must explain *why* the user needs to perform the action; WAIT takes a quoted condition string describing what to look for on screen rather than a second count; coordinate COORDS commands cause a crosshair overlay to be shown to the user; and the visual-confirmation rule asks the AI to verify via the next screenshot that the human performed the step correctly. `BuildSystemPrompt` applies all mode-dependent replacements **before** universal replacements (e.g. `{normalizeSize}`) so that placeholders inside expanded mode strings are resolved correctly.
* `SecretStorage.cs`: Cross-platform encrypted storage for API keys and other secrets. Defines the `ISecretProvider` interface (`SaveSecret`, `LoadSecret`, `SecretExists`, `DeleteSecret`) and its `SecretsHandler` implementation. Each secret is stored as an AES-encrypted value within a `.json` file in the OS `LocalApplicationData` folder under `ThioUniversalAgent/`. The encryption key is derived from a **password hash** (never the raw password) via PBKDF2-SHA256 with 100,000 iterations. The hash is computed client-side in the browser using `SubtleCrypto` SHA-256, so the plaintext password never reaches the server. The hash can optionally be persisted in `localStorage` for auto-unlock on next visit. This design keeps the encrypted files isolated on the server's filesystem, separate from the browser, guarding against XSS and rudimentary credential stealers even when the "remember hash" option is enabled.
* `RuntimeHandlers.cs`: Application-level functions such as finding an available port to launch on.
* `HotkeyService.cs`: `IHostedService` that reads `AppConfig.Hotkeys` on startup and registers the configured Pause/Resume and Stop hotkeys via `IHotkeyProvider`. Routes `HotkeyPressed` callbacks to `AgentSessionManager`. Exposes `ReloadHotkeys()` to swap registrations after a config change. Also contains the internal `HotkeyStringParser` helper, which parses human-readable strings like `"Ctrl+Shift+P"` into `HotkeyModifiers` flags and Win32 virtual-key codes.

### `/Models/` (Data Structures)
* `AgentSession.cs`: Holds state for a running task (Goal, Status, History, Cancel tokens, Pause states).
* `AgentAction.cs`: Enums (`AgentActionKind`) and records defining what the AI wants to do.
* `Config.cs`: Deeply nested typed configuration (`AppConfig`, `GeminiConfig`, `OpenAIConfig`, `OpenAICompatibleConfig`, `AnthropicConfig`, `OnnxConfig`, `AgentConfig`). Includes the `AiProviderType.OpenAICompatible` and `AiProviderType.Onnx` provider options and uses custom `[ConfigField]` attributes to auto-generate the frontend UI settings page.
* `AiConversationTypes.cs`: Shared conversation and response data types used by all AI providers: `AiChatRole` (enum), `AiChatMessage` (role + optional text/image), `AiConversation` (ordered message history with `AddUserMessage`/`AddModelMessage`), `AiResponse` (success flag + text + optional `TokenUsage`), `AiRequestOptions` (per-call overrides such as `MaxOutputTokens`), and `TokenUsage` (prompt/completion/total/thinking counts with a `+` operator for accumulation).
* `Globals.cs`: Placeholder `static` class for future process-wide statics; currently empty.
* `ScreenCoordinate.cs` & `Screenshot.cs`: Mathematical wrappers for translating between virtual desktop space, normalized AI space (0-1000), and image-local pixels.

### `/OS_Windows/` (Platform Implementations)
* `WindowsHotkeyProvider.cs`: Windows implementation of `IHotkeyProvider`. Creates an invisible message-only window (`HWND_MESSAGE`) on a dedicated STA background thread so that `RegisterHotKey` and the `WndProc` callback share the required Win32 thread affinity. Marshals `RegisterHotkey`/`UnregisterHotkey` calls from any thread onto the pump thread via custom `WM_APP` messages, with a `TaskCompletionSource` handshake to surface Win32 errors back to the caller.
* `WindowsInputProvider.cs`: Heavy P/Invoke logic. Maps `SendInput` for keyboard/mouse events, handles Unicode text typing, and scroll wheel messages.
* `WindowsScreenProvider.cs`: Uses GDI (`BitBlt`) to capture screens rapidly and accounts for multi-monitor virtual desktop coordinates.

### `/Endpoints/` (Minimal APIs)
* `AgentEndpoints.cs`: Routes for `/api/agent/...` (start, stop, status). Houses the complex SSE (`text/event-stream`) logic for real-time frontend updates. On session start it applies provider-specific API key/model overrides; `OpenAICompatible` and `Onnx` may run without an API key.
* `ConfigEndpoints.cs`: Generates dynamic JSON schema via reflection from `ConfigField` attributes, allowing the frontend to build a settings menu automatically. The provider sections now include `gemini`, `openai`, `openaiCompatible`, `anthropic`, and `onnx`.
* `SecretsEndpoints.cs`: Four routes under `/api/secrets/` that front `ISecretProvider` — `POST /save` (encrypt and persist), `POST /load` (decrypt and return, 401 on wrong password / 404 if not found), `GET /{key}/exists` (existence check without decryption), and `DELETE /{key}` (permanently remove a secret file). Secret keys follow the `{sectionKey}_{fieldKey}` convention (e.g. `gemini_apiKey`).
* `TestEndpoints.cs`: Isolated `/api/test/...` endpoints purely for the web-based debugging tools. Provider-override testing can instantiate throwaway clients, the OpenAI-compatible path reuses `OpenAIProvider` with the configured endpoint URL, and ONNX testing can instantiate a throwaway local model provider from the active config.

### `/Extensions/` (Utility Extensions)
* `ByteArrayExtensions.cs`: Extension methods on `byte[]`: `ToBase64()` (plain Base64 string) and `ToBase64DataUri(mimeType)` (data URI suitable for HTML `<img>` tags or AI vision API payloads).

### `/wwwroot/` (Frontend)
* `Agent.html`: The main control panel. Connects to the SSE stream to display live thoughts, actions, and debug images.
* `Config.html`: Dynamically renders inputs based on the backend schema. Saves non-secret settings to `localStorage` and syncs with the C# backend. The OpenAI-compatible provider section exposes a configurable endpoint URL and optional API key, while the ONNX section exposes a local model folder path plus execution/runtime controls. API key fields (`IsPassword = true`) are managed separately via the **API Key Vault** UI: the user enters a vault password, it is hashed client-side with `SubtleCrypto` SHA-256, and the hash is used to save/load secrets through `SecretsEndpoints`. On page load the vault attempts auto-unlock if a remembered hash is present in `localStorage`. The "Reset + Erase API Keys" button additionally calls `DELETE /api/secrets/{key}` for each known secret before resetting other settings.
* `/css/` and `/js/`: Style and script parts used in the html front end.
* `/Testing/`: Sandboxed HTML pages for testing Chat, Screenshot bounding boxes, and Coordinate prompting in isolation.