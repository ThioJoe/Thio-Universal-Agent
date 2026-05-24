# AI Context: Thio Universal Agent (TUA)

## 1. Project Overview
**Thio Universal Agent** is an autonomous, cross-platform AI desktop assistant capable of host-native execution. It uses a Vision-Language Model (Google Gemini) to visually "see" the computer screen and perform tasks by taking physical control of the mouse and keyboard.

**Tech Stack:**
* **Backend:** C# / .NET 10, ASP.NET Core Minimal APIs.
* **Frontend:** Vanilla HTML/CSS/JS (embedded in the assembly via `wwwroot`). No modern JS frameworks.
* **OS Integration:** Windows (implemented via P/Invoke `user32.dll`, `gdi32.dll`), architected via interfaces for future cross-platform support.
* **AI Provider:** Currently only Google Gemini API (`generativelanguage.googleapis.com`) using structured text/vision prompting, but with scaffolding to accept others later.

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

### `/Interfaces/` (The Abstraction Layer)
* `IAiProvider.cs`: Contract for the LLM (SendPrompt, StartConversation, ContinueConversation with/without images).
* `IScreenProvider.cs`: Contract for capturing screenshots and enumerating monitors.
* `IInputProvider.cs`: Contract for simulating OS events (mouse clicks, drags, typing text, keyboard combos).
* `ISystemProvider.cs`: Contract for retrieving OS details (name, build) to feed the AI context.

### `/Handlers/` (Core Logic)
* `AgentSessionManager.cs`: Singleton that creates `AgentSession`s and spawns the `AgentLoop` on background tasks. Handles stop/pause/resume requests from the UI.
* `AgentLoop.cs`: The core execution engine. Manages the Observe-Think-Act loop, handles token bloat via context resets, and triggers parsing retries on failure.
* `AgentActionParser.cs`: Parses the AI's exact text output into strongly-typed `AgentAction` records. Handles both single `ACTION:` and batched `QUEUE:` formats.
* `AgentActionExecutor.cs`: Takes an `AgentAction`, routes it through the `CoordinatePrompter` (if spatial resolution is needed), and then executes it via `IInputProvider`.
* `CoordinatePrompter.cs`: Complex visual logic. Determines exactly *where* to click based on the AI's target description. Uses modes like `Zoom` (drawing a grid over the screen) or `DirectAutoNormalize` (translating 1000x1000 normalized coordinates back to physical pixels).
* `ScreenshotProcessing.cs` (part of `CoordinatePrompter`): Handles SkiaSharp image manipulation, drawing grids, crosshairs, and cropping for zoom modes.
* `AgentPromptBuilder.cs`: Constructs the massive system prompt teaching the AI its tools, rules, and formats.
* `SecretStorage.cs`: Cross-platform encrypted storage for API keys and other secrets. Defines the `ISecretProvider` interface (`SaveSecret`, `LoadSecret`, `SecretExists`, `DeleteSecret`) and its `SecretsHandler` implementation. Each secret is stored as an AES-encrypted value within a `.json` file in the OS `LocalApplicationData` folder under `ThioUniversalAgent/`. The encryption key is derived from a **password hash** (never the raw password) via PBKDF2-SHA256 with 100,000 iterations. The hash is computed client-side in the browser using `SubtleCrypto` SHA-256, so the plaintext password never reaches the server. The hash can optionally be persisted in `localStorage` for auto-unlock on next visit. This design keeps the encrypted files isolated on the server's filesystem, separate from the browser, guarding against XSS and rudimentary credential stealers even when the "remember hash" option is enabled.

### `/Models/` (Data Structures)
* `AgentSession.cs`: Holds state for a running task (Goal, Status, History, Cancel tokens, Pause states).
* `AgentAction.cs`: Enums (`AgentActionKind`) and records defining what the AI wants to do.
* `Config.cs`: Deeply nested typed configuration (`AppConfig`, `GeminiConfig`, `AgentConfig`). Uses custom `[ConfigField]` attributes to auto-generate the frontend UI settings page.
* `ScreenCoordinate.cs` & `Screenshot.cs`: Mathematical wrappers for translating between virtual desktop space, normalized AI space (0-1000), and image-local pixels.

### `/OS_Windows/` (Platform Implementations)
* `WindowsInputProvider.cs`: Heavy P/Invoke logic. Maps `SendInput` for keyboard/mouse events, handles Unicode text typing, and scroll wheel messages.
* `WindowsScreenProvider.cs`: Uses GDI (`BitBlt`) to capture screens rapidly and accounts for multi-monitor virtual desktop coordinates.

### `/Endpoints/` (Minimal APIs)
* `AgentEndpoints.cs`: Routes for `/api/agent/...` (start, stop, status). Houses the complex SSE (`text/event-stream`) logic for real-time frontend updates.
* `ConfigEndpoints.cs`: Generates dynamic JSON schema via reflection from `ConfigField` attributes, allowing the frontend to build a settings menu automatically.
* `SecretsEndpoints.cs`: Four routes under `/api/secrets/` that front `ISecretProvider` — `POST /save` (encrypt and persist), `POST /load` (decrypt and return, 401 on wrong password / 404 if not found), `GET /{key}/exists` (existence check without decryption), and `DELETE /{key}` (permanently remove a secret file). Secret keys follow the `{sectionKey}_{fieldKey}` convention (e.g. `gemini_apiKey`).
* `TestEndpoints.cs`: Isolated `/api/test/...` endpoints purely for the web-based debugging tools.

### `/wwwroot/` (Frontend)
* `Agent.html`: The main control panel. Connects to the SSE stream to display live thoughts, actions, and debug images.
* `Config.html`: Dynamically renders inputs based on the backend schema. Saves non-secret settings to `localStorage` and syncs with the C# backend. API key fields (`IsPassword = true`) are managed separately via the **API Key Vault** UI: the user enters a vault password, it is hashed client-side with `SubtleCrypto` SHA-256, and the hash is used to save/load secrets through `SecretsEndpoints`. On page load the vault attempts auto-unlock if a remembered hash is present in `localStorage`. The "Reset + Erase API Keys" button additionally calls `DELETE /api/secrets/{key}` for each known secret before resetting other settings.
* `/Testing/`: Sandboxed HTML pages for testing Chat, Screenshot bounding boxes, and Coordinate prompting in isolation.