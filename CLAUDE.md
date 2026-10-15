# ClickyWin — Windows port of farzaa/clicky (C# WPF, .NET 10)

Hold Ask (Ctrl+Shift+Space) → mic → AssemblyAI → Claude (screenshots) → streamed voice + blue dot pointing.
Hold Dictate (Ctrl+Win) / Write (Alt+Win) / Prompt (Shift+Win, speech → concise AI prompt) → text typed at the caret. Shortcuts via WH_KEYBOARD_LL (`KeyboardHookService`).
Base: emreyilmaz46/clicky_windows (MIT), remote `upstream`. Work on branch `dev`; tag `v0.0-upstream` = original.

## Layout
- `appsettings.json` (shipped, next to exe): prompts, hotkey, endpoints, models, screenshot limits.
- `%APPDATA%\ClickyWindows\settings.json`: user choices + API keys (DPAPI-encrypted), written by `SetupWindow`.
- `Services/CompanionManager.cs`: per-press Session, state machine, pipeline on thread pool.
- `Services/ClaudeService.cs`: official Anthropic SDK; Haiku 5.5 first (thinking off), `[ESCALATE]` → Opus 5.5 (fallbacks "default").
- `Services/AudioCaptureService.cs`: one reused WASAPI client (fast restart), 100 ms PCM16/16k frames.
- `Services/AssemblyAIService.cs`: single send loop (pre-roll), ForceEndpoint + Terminate, all turns joined.
- `Services/SpeechService.cs`: ElevenLabs, falls back to Windows SAPI voices.
- `Services/PointRefiner.cs`: snaps [POINT] guesses onto UIA elements by name (taskbar/foreground), else a full-res zoom crop.
- `Services/ScreenCaptureService.cs`: images kept under Claude no-resize limits so [POINT] coords map back exactly.
- Computer use (2026-10-15): Ask reply ends with `[DO: task]` → `AgentController` (Allow card → countdown → run) → `Services/ComputerAgent.cs` (computer_toolset_20260801 + `ask_user` tool, primary monitor at ≤1280x800, coords mapped back) → `Helpers/InputDriver.cs` (SendInput). `UserInputWatcher` stops it on the user's real mouse/keys/Esc. `AgentCard` = approval/confirm/banner (excluded from capture). Hands-free task session (`TaskSession.cs`): double-tap Ask → mic stays open, each AssemblyAI turn is a command (tasks pre-approved, voice yes/no/options via `AgentCard.TryAnswer`, "stop"/"para" stops a task, "end session"/Esc/double-tap/3 min silence ends; speech heard while Clicky talks is ignored). Test: `--session-say "line|!mid-task line|end session"`. Test without the hotkey: `--do "task"` (agent directly) or `--ask "question"` (typed question through the Ask pipeline). Settings: AgentModel (Opus 5.5 default, Haiku 5.5 cheap), AgentAlwaysAllow, AgentCostCap.
- `Helpers/L.cs`: ES/EN strings ("Auto" follows the language last spoken).

## Rules
- Never put API keys in chat, logs or tracked files; users enter them in the Setup window.
- UI updates from background threads: `Dispatcher.InvokeAsync`, never `Invoke`.
- Tests: scratch harness compiles Services/Helpers/Models/Settings against a fake local Claude SSE server.

## Open phases
See `docs/ROADMAP.md` (gap analysis vs mac Clicky/HeyClicky; P1 + D1–D4 + P3–P5 done 2026-10-14).
Launch flags: `--settings` opens Setup, `--panel` opens the tray panel (screenshots/tests).
Installer: `installer\build.ps1` → `dist\Clicky-<Version>-Setup.exe` (self-contained `Dist` profile + Inno Setup 6, dark wizard; version from csproj `<Version>`). `-Install` also upgrades the installed copy (%LOCALAPPDATA%\Programs\Clicky) in place; bump `<Version>` first.
UI: `Themes/Dark.xaml` = mac DesignSystem tokens (implicit styles for all windows); mac icon in `Assets/` (MIT, Farza; see THIRD_PARTY_NOTICES.txt). Overlay/Setup are excluded from or hard to screenshot: render XAML offscreen to check.
Release build: `dotnet publish -p:PublishProfile=Release` → `publish\` (R2R; no startup gain measured, ~1 s either way).
Overlay = one 320x200 window that moves with the dot (2026-10-15; idle 42 MB Task Manager / 67 MB private bytes, was 56/100). Remaining: multi-monitor (skipped by user).
