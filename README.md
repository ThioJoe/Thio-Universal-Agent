# 🤖 Thio's Universal Agent

An autonomous AI desktop assistant capable of host-native execution using Vision-Language Models (Gemini, ChatGPT, Claude).

## How It Works (Why "Universal"?)

**Simply put, it works across the whole computer.** Unlike most AI "computer use" tools which only work in a browser or via command line, this uses the computer like you do.

It controls Windows purely through visual perception and GUI interaction. By interpreting raw pixels and sending hardware-level input (mouse movements, clicks, keystrokes), it operates exactly like a human would. This makes it **universally compatible** with any graphical application on your host machine.

It does **not** require UI Automation APIs, screen reader support, or application-specific hooks. 

## How It's _Built Different_

* **Single Portable `Exe` - NO Installation Required** - Releases are compiled with single-exe mode, it's just one file.
   * _No_ bloated 🤡Python🤡 or 🤡NodeJS🤡 or other environment installation.
   * _ZERO_ Third-Party Dependencies 😤 (Uses core .NET Libraries and official Microsoft packages ONLY) _#AllMyHomiesHateDependencies_
   * Ideal for running in VMs and Sandboxes. Spin up a fresh sandbox instance and it's ready to go, just set the API key.
* **Visual-Only Operation**: Works on any app, regardless of underlying framework, because it relies strictly on screen pixels.
* **Multiple AI Providers**: Supports Google Gemini (default), OpenAI (ChatGPT), and Anthropic (Claude).

### Additional Features:
* **Global Hotkey Support**: Pause, resume, or terminate the agent instantly even when the web UI is minimized.
* **Live Agent Redirection**: Issue mid-flight text instructions to the agent to override or adjust its current execution plan.
* **Config Import/Export**: Export your config options to a file and import it. Settings are also stored in the browser to survive between sessions.
* **.NET Based - Theoretically Cross Platform** - Currently the only input providers are set up for Windows, but it could work with MacOS or even Linux if someone implemented the interfaces for their APIs.

## Demonstration

<p align="center">
An example of how it can queue multiple actions at once, while accurately identifying exact coordinates.
<br/><br/>
<img width="1000" alt="Demo Gif" src="https://github.com/user-attachments/assets/c3109876-93b3-4ebe-84a5-1598f3b7874d" />
</p>
  
<b>Prompt:</b> <i>In MS Paint draw a self portrait with multiple colors with the brush tool. Use the full action queue when possible.</i><br/>
<b>Model:</b> Gemini 3.5 Flash


----------

## How it Works

### The Observe-Think-Act Loop:
1. **Observe:** Captures the current desktop state as an image.
2. **Think:** Sends the screenshot and prompt to the AI to determine the next action.
3. **Use Input Tools:** The AI chooses the desired tool (e.g., `LEFT_CLICK`, `TYPE_TEXT`) and outputs the coordinates. Through some clever prompting tricks, this is highly reliable and accurate even with high resolution (4K) screenshots
4. **Act:** Simulates physical hardware inputs via native OS APIs (`user32.dll` / `gdi32.dll`).

### Special Sauce
- **Queued actions** - The AI can queue multiple actions where appropriate to speed through multiple similar actions, such as drawing quickly.
- **Accurate Clicking** - Prompts optimized so even on a 4K screen, the latest models can be spot-on with coordinates even for small UI elements.

### Usage / Security Notice

**⚠️ <ins>Prototype software - Not intended for production use.</ins>**<br/>
This application executes real, unauthenticated OS-level input events. Do not expose the web server port to the internet or untrusted networks. Operate only in a supervised, isolated local environment.

## Usage Instructions

1. Run the compiled executable. A local web interface will initialize (default: `http://localhost:5112`).
2. Navigate to the **Config** menu in the web UI.
3. Enter your required API key (e.g., Google Gemini) and adjust any desired operational parameters (model, temperature, coordinate mode). Click **Save to browser**.
4. Navigate to the **Agent Control** panel.
5. Select a target monitor, enter your task directive in the **Goal** field, and click **Start**.
6. **Interrupting Execution:** Use the Pause/Stop buttons in the UI, or the default global hotkeys (`Ctrl+Shift+Alt+P` to pause, `Ctrl+Shift+Alt+S` to stop).

## Screenshots
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

## Licensing - Personal Use Only

This app is source-available. Free for PERSONAL use only. You may not use it for commercial purposes (it's not ready for production anyway).

Are you a big tech company who wants to buy it and build it out properly? I could be convinced. 

<p align="center">
<img width="550" alt="image" src="https://github.com/user-attachments/assets/32c97850-4507-4e11-a899-0a9fba678b62" />
</p>
I also have a lot of experience with the Windows API to help with development, as evidenced by my other projects here on GitHub.
