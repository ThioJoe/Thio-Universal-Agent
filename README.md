<h1 align = 'center'>
    <img 
        src = '.github/assets/Icon.png' 
        height = '100' 
        width = '100' 
        alt = 'Thio Universal Agent Icon' 
    >
    <br>
    Thio's Universal Agent
    <br>
</h1>

### An AI desktop assistant app capable of interacting with your entire computer (and any apps) like you do.

### ➤ Also, it's just one portable .exe file, no bloated install required.

# What It Does (And Why "Universal"?)

**Simply put, it lets your AI works across the whole computer.** Unlike most AI "computer use" tools which only work in a browser or via command line, this uses the computer like you do.

It controls Windows purely through visual perception and GUI interaction. By interpreting raw pixels and sending hardware-level input (mouse movements, clicks, keystrokes), it operates exactly like a human would. This makes it **universally compatible** with any graphical application on your machine.

## Optional: Human Control Only Mode

It now supports two ways to use it: a **Human Control Only Mode** that is enabled by default, where the AI guides you step-by-step and shows where to click without sending any real input itself, and an **autonomous mode** where it can move the mouse and type on its own.

There is also a **dedicated Human-Only exe version**, in cases where you never even want the _option_ to operate in autonomous mode. In this version, the input code is physically excluded from the exe. (The other main exe can still do both)

Also See: [Planned Features Page](https://github.com/ThioJoe/Thio-Universal-Agent/wiki/Planned-Features)

## Demonstration

<p align="center">
Example of it queuing multiple actions at once, while accurately clicking exact coordinates within the <i>entire</i> 4K screen.
<br/><br/>
<img width="1000" alt="Demo Gif" src="https://github.com/user-attachments/assets/c3109876-93b3-4ebe-84a5-1598f3b7874d" />
</p>
<p align="center">
<b>Prompt:</b> <i>In MS Paint draw a self portrait with multiple colors with the brush tool. Use the full action queue when possible.</i><br/>
<b>Model:</b> Gemini 3.5 Flash
</p>

# Example Use Cases

- Ask it to _show_ you how to do something instead of just getting a text description like other AIs.
- Ask it to troubleshoot some error you're getting and figure out the cause.
- Ask it to visually find an image in a folder of un-labelled files, based on a description.
- Tell it to check every 10 seconds if a video render is done, then when it is, copy the file somewhere.
    - (Though not recommended to leave unattended, unless in a controlled sandbox environment)


# Frequently Asked Questions

### **Q:** Is this like OpenClaw or Hermes?
**A:** No, this is not intended to be a 24/7 running agent. It also doesn't rely on CLI/Shell commands. It's meant for individual tasks or problems you'd normally have to do yourself. 

### **Q:** How long _can_ it run?
**A:** There's not actually a limit. You can set the max number of steps to any number in the settings. The default is arbitrarily set to 100 steps.

### **Q:** Which AI Services are supported?
**A:** Currently ChatGPT, OpenAI-compatible APIs, Gemini, Claude, and local ONNX models (ChatGPT/Gemini/Claude require your own API key). Currently it seems Gemini works the best, especially `gemini-flash-latest`.

### **Q:** Doesn't this use a ton of tokens?
**A:** Sort of, but not as much as you might think. Each step is maybe 3k tokens, but input tokens are cheaper. Completion tokens are usually as few as 50, up to a few hundred for many queued actions. The big factor is how many thinking tokens are used.
  - For example, with Gemini 3.5 Flash, it seems each step with a single action costs about 1 cent or less.
  - I recommend setting thinking to the minimum, but even then it may use a few thousand tokens if it's going to queue up a lot of actions.
  - There's also context mitigation logic, such as summarization context every X steps, and removing past images from the context window (Both can be disabled in settings)

### **Q:** Can I do other stuff while it's going?
**A:** Not really. It won't block your mouse or keyboard input or anything. But it's best to not touch anything while it's running to prevent interfering. You can do little stuff between steps to help it though, like if it clicked the wrong thing, click it yourself.
  - You can use global keyboard hotkeys to pause or stop it at any time.

### **Q:** What is Human Only Control Mode?
**A:** By default it starts in **Human Control Only Mode**, where the AI tells you what to do and where to click while you perform the actions yourself. It draws crosshairs for where to click, and boxes around where to enter text. It also displays a small text box you can copy the recommended text from. This can be switched to fully autonomous mode in the config settings.

--------

# How It's _Built Different_

* **Single Portable `Exe` - NO Installation Required** - Releases are compiled with single-exe mode, it's just one file.
   * _No_ bloated 🤡Python🤡 or 🤡NodeJS🤡 or other environment installation.
   * _ZERO_ Third-Party Dependencies 😤 (Uses core .NET Libraries and official Microsoft packages only) _#AllMyHomiesHateDependencies_
  * Ideal for running in VMs and Sandboxes. Spin up a fresh sandbox instance and it's ready to go, just configure your provider settings.
* **Visual-Only Operation**: Works on any app, regardless of underlying framework, because it relies strictly on screen pixels.
* **Multiple AI Providers**: Supports Google Gemini (default), OpenAI (ChatGPT), OpenAI-compatible chat-completions services, Anthropic (Claude), and local ONNX Runtime GenAI models.

### Additional Features:
* **Human Control Only Mode**: Enabled by default. The AI guides you with step-by-step instructions and on-screen click markers, but it never sends mouse or keyboard input until you disable that mode.
* **Global Hotkey Support**: Pause, resume, or terminate the agent instantly even when the web UI is minimized.
* **Live Agent Redirection**: Issue mid-flight text instructions to the agent to override or adjust its current execution plan.
* **Config Import/Export**: Export your config options to a file and import it. Settings are also stored in the browser to survive between sessions.
* **.NET Based - Theoretically Cross Platform** - Currently the only input providers are set up for Windows, but it could work with MacOS or even Linux if someone implemented the interfaces for their APIs.

--------

# Comparison With Computer-Use Tools

<table>
  <thead>
    <tr>
      <th align="left">Feature</th>
      <th align="center">Thio's Universal Agent</th>
      <th align="center">OpenAI Operator</th>
      <th align="center">Google Gemini Computer Use</th>
      <th align="center">Anthropic Computer Use</th>
      <th align="center">Microsoft Research UFO (UFO³)</th>
    </tr>
  </thead>
  <tbody>
    <tr valign="top">
      <td align="left"><strong>Ready-to-Run App</strong></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Ready Out of the Box</strong></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>N/A<br/><sub>(Web Hosted)</sub></strong></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Dev API,<br/>Not an app<sup>4</sup></strong></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Dev API,<br/>Not an app<sup>5</sup></strong></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Research Framework</strong></td>
    </tr>
    <tr valign="top">
      <td align="left"><strong>Setup Difficulty</strong></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Easy</strong><br/><sub>(Just launch the portable <code>.exe</code>)</sub></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Easy</strong><br/><sub>(Log into web service)</sub></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Hard</strong><br/><sub>(Requires Python, Playwright)</sub><sup>7</sup></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Hard</strong><br/><sub>(Requires custom tooling)</sub><sup>8</sup></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>Hard</strong><br/><sub>(Conda, pip installs, YAML configuration)</sub><sup>9</sup></td>
    </tr>
    <tr valign="top">
      <td align="left"><strong>Computer-Wide Control</strong></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Yes</strong></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>No</strong><br><sub>(Web-Only)</sub><sup>10</sup></td>
      <td align="center"><img src=".github/assets/x-red.svg" width="16"><br/><strong>No</strong><br><sub>(Web-Only)</sub><sup>1</sup></td>
      <td align="center"><img src=".github/assets/dash-orange.svg" width="16"><br/><strong>Not By Itself</strong><br><sub>(Needs external app to handle input)</sub></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Yes</strong></td>
    </tr>
    <tr valign="top">
      <td align="left"><strong>Recommended / Max Resolution</strong></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>4K+</strong><br><sub>(Depends on chosen model)</sub></td>
      <td align="center"><img src=".github/assets/dash-orange.svg" width="16"><br/><strong>1600x900</strong><br><sub>(Recommended Resolution)</sub><sup>6</sup></td>
      <td align="center"><img src=".github/assets/dash-orange.svg" width="16"><br/><strong>1440x900</strong><br><sub>(Recommended Resolution)</sub><sup>2</sup></td>
      <td align="center"><img src=".github/assets/dash-orange.svg" width="16"><br/><strong>~2560x1440</strong><br><sub>(Max For Opus 4.7)</sub><sup>3</sup></td>
      <td align="center"><img src=".github/assets/check-green.svg" width="16"><br/><strong>Theoretically Any Resolution</strong><br><sub>(Hybrid UIA + Vision)</sub></td>
    </tr>
    <tr>
      <td align="left"><strong>Supported Models</strong></td>
      <td valign="top" align="center"><img src=".github/assets/check-green.svg" width="16"> <strong>Multiple</strong><br><sub>(Gemini, OpenAI, OpenAI-Compatible, Claude, Local ONNX)</sub></td>
      <td align="center"><strong>OpenAI Only</strong></td>
      <td align="center"><strong>Gemini Only</strong></td>
      <td align="center"><strong>Claude Only</strong></td>
      <td valign="top" align="center"><img src=".github/assets/check-green.svg" width="16"> <strong>Multiple</strong><br><sub>(Gemini, OpenAI, OpenAI-Compatible, Claude)</sub></td>
    </tr>
  </tbody>
</table>

<p>
<sub>1. Gemini computer use announcement post states "<a href="https://blog.google/innovation-and-ai/models-and-research/google-deepmind/gemini-computer-use-model/">It is not yet optimized for desktop OS-level control.</a>"</sub>"<br/>
<sub>2. Gemini docs state "<a href="https://ai.google.dev/gemini-api/docs/computer-use#execute-actions">The recommended screen size ... is (1440, 900).</a>" and performance "may be impacted" with other resolutions.</sub>"<br/>
<sub>3. For Opus 4.7 -  <a href="https://claude.com/blog/best-practices-for-computer-and-browser-use-with-claude#:~:text=Max%20long%20edge%3A%202576%20pixels">Max long edge: 2576 pixels & Max total pixels: 3.75 megapixels</a></sub>"<br/>
<sub>4. Gemini docs state: "<a href="https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/computer-use#:~:text=you%20need%20to%20write%20the%20client%2Dside%20application%20code%20to%20receive%20the%20Computer%20Use%20model%20and%20tool%20function%20call%20and%20execute%20the%20corresponding%20actions">you need to write the client-side application code to ... execute the corresponding actions</a>"</sub>"<br/>
<sub>5. Anthropic Computer Use has a <a href="https://github.com/anthropics/claude-quickstarts/tree/main/computer-use-demo">demo app implementation</a>, but requires MacOS with Python, or setup in Docker</sub>"<br/>
<sub>6. OpenAI recommends 1440x900 or 1600x900 for optimal click accuracy (see <a href="https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/computer-use">Azure OpenAI Computer Use Guide</a>).</sub>"<br/>
<sub>7. Gemini Computer Use requires <a href="https://github.com/google-gemini/computer-use-preview#1-installation">Python + dependencies, and downloading browser binaries via Playwright.</a></sub>"<br/>
<sub>8. Anthropic's Computer Use API only outputs proposed tool calls; <a href="https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use-tool#:~:text=When%20you%20use%20computer%20use%2C%20Claude%20doesn%27t%20directly%20connect%20to%20this%20environment">developers must implement their own OS-level execution harness</a>.</sub>"<br/>
<sub>9. UFO³ setup involves <a href="https://github.com/microsoft/UFO/blob/main/galaxy/README.md#%EF%B8%8F-step-1-installation">installing Conda/Python</a>, and <a href="https://github.com/microsoft/UFO/blob/main/galaxy/README.md#%EF%B8%8F-step-3-configure-device-agents">YAML configurations</a>.</sub>"<br/>
<sub>10. <a href="https://openai.com/index/introducing-operator/">OpenAI Operator</a> (now called ChatGPT agent mode) runs within a virtual web browser hosted by OpenAI.</sub><br/>
</p>

----------

# How it Works

### The Observe-Think-Act Loop:
1. **Observe:** Captures the current desktop state as an image. It does **not** require UI Automation APIs, screen reader support, or application-specific hooks.
2. **Think:** Sends the screenshot and prompt to the AI to determine the next action.
3. **Use Input Tools:** The AI chooses the desired tool (e.g., `LEFT_CLICK`, `TYPE_TEXT`) and outputs the coordinates. Through some clever prompting tricks, this is highly reliable and accurate even with high resolution (4K) screenshots
4. **Act:** Simulates physical hardware inputs via native OS APIs (`user32.dll` / `gdi32.dll`).

### Special Sauce
- **Queued actions** - The AI can queue multiple actions where appropriate to speed through multiple similar actions, such as drawing quickly.
- **Accurate Clicking** - Prompts optimized so even on a 4K screen, the latest models can be spot-on with coordinates even for small UI elements.

### Security Notice

**⚠️ <ins>Prototype software - Not intended for production use.</ins>**<br/>
This application executes real, unauthenticated OS-level input events. Do not expose the web server port to the internet or untrusted networks. Operate only in a supervised, isolated local environment.

# Setup & Usage Instructions

### How to Download
1.  Go to the [Releases](https://github.com/ThioJoe/Thio-Universal-Agent/releases) page.
2.  For the latest release, look under `Assets` and download `Thio-Universal-Agent.exe`.

### Usage Instructions

Note: Configure the provider you want to use in the Config page. ChatGPT, Gemini, and Claude need an API key. OpenAI-compatible endpoints can use a custom URL and may omit the key for local or self-hosted services. Local ONNX models instead need a model folder path that contains `genai_config.json`; for screenshot-based agent tasks, use a vision-capable ONNX Runtime GenAI export.

1. Run the compiled executable. A local web interface will initialize (default: `http://localhost:51122`).
2. Navigate to the **Config** menu in the web UI.
3. Choose your provider and configure its settings. For ChatGPT, Gemini, and Claude, enter your API key. For OpenAI-compatible services, enter the chat-completions endpoint URL and optionally an API key. For local ONNX, set the model folder path and optional execution provider (for example `DML` on Windows), then adjust any desired operational parameters (model, temperature, coordinate mode). 
  - **Human Control Only Mode is enabled by default** near the top of Config. Leave it on if you want guided/manual control, or turn it off if you want the agent to click and type autonomously. Click **Save to browser**.
4. Navigate to the **Agent Control** panel. The page shows a prominent Human Control Mode status banner so you can confirm whether the run will be guided or autonomous before starting.
5. Select a target monitor, enter your task directive in the **Goal** field, and click **Start**.
6. **Interrupting Execution:** Use the Pause/Stop buttons in the UI, or the default global hotkeys (`Ctrl+Shift+Alt+P` to pause, `Ctrl+Shift+Alt+S` to stop).

# Screenshots
<p align="center">
  <img width="800" alt="image" src="https://github.com/user-attachments/assets/66264587-6392-4ef9-a61a-845d0f51e045" />
</p>
<p align="center">
  <img width="800" alt="image" src="https://github.com/user-attachments/assets/4f3d90ef-6e24-439f-965b-f0cc8acb5283" />
</p>

## Development & Compilation

### Requirements:
* Visual Studio 2026
* .NET 10.0 SDK

### Instructions:

1. Open the solution file (`Thio-Universal-Agent.slnx`) in Visual Studio 2026.
2. Select your desired build configuration (`Debug` or `Release`).
3. Compile and run the solution.

# Licensing - Personal Use Only

This app is source-available. Free for PERSONAL use only. You may not use it for commercial purposes (it's not ready for production anyway).

**Are you a big tech company who wants to buy it and/or bring me on to build it out properly? I could be convinced.**

<p align="center">
<img width="450" src="https://github.com/user-attachments/assets/c6a61205-ba4e-4a7a-b85a-1328a1dec761" />
</p>
