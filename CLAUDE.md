# ClickyWin — Windows port of farzaa/clicky (C# WPF, .NET 10)

Check: none (no test project; build = `installer\build.ps1`, only to ship). Status and to-do: `docs/STATUS.md`.

Hold Ask (Ctrl+Shift+Space) → mic → AssemblyAI → Claude (screenshots) → streamed voice + blue dot pointing.
Hold Dictate (Ctrl+Win) / Write (Alt+Win) / Prompt (Shift+Win, speech → concise AI prompt) → text typed at the caret. Shortcuts via WH_KEYBOARD_LL (`KeyboardHookService`).
Base: emreyilmaz46/clicky_windows (MIT), remote `upstream`. Work on `main` (old feature branches merged and deleted); tag `v0.0-upstream` = original.

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
- Computer use (2026-10-15): Ask reply ends with `[DO: task]` → `AgentController` (Allow card → countdown → run) → `Services/ComputerAgent.cs` (computer_toolset_20260801 + `ask_user` tool, primary monitor at ≤1280x800, coords mapped back) → `Helpers/InputDriver.cs` (SendInput). `UserInputWatcher` stops it on the user's real mouse/keys/Esc. `AgentCard` = approval/confirm/banner (excluded from capture). Hands-free task session (`TaskSession.cs`): double-tap Ask → mic stays open, each AssemblyAI turn is a command (tasks pre-approved, voice yes/no/options via `AgentCard.TryAnswer`, "stop"/"para" stops a task, "end session"/Esc/double-tap/3 min silence ends; speech heard while Clicky talks is ignored). Test: `--session-say "line|!mid-task line|end session"`. Test without the hotkey: `--do "task"` (agent directly) or `--ask "question"` (typed question through the Ask pipeline). Settings: AgentModel (Sonnet 5.5 default since P2; Opus never drives by design, Haiku 5.5 cheap), AgentAlwaysAllow, AgentCostCap.
- Ranks (P2, `docs/PLAN_RANKS_TEACHER.md`): Ask reply `[DO:runner|teacher|worker|engineer (long): task]` (plain `[DO:]` = worker) → `ClaudeService.ParseDoTask` → `DoTask` → `AgentController.RouteAsync`. Runner = `Services/Runner.cs` ("youtube …" results page + one Haiku click run, "open <app|site>"), else worker. Teacher = `TeacherLesson.Start` (from `CompanionManager`); engineer = `RunEngineerAsync` (P3). Agent ladder: Haiku `hand_off` → `Agent.HandOffModel` (Sonnet); Sonnet `consult` tool → `Agent.BossModel` (Opus, text only, max 3); `long` = one Opus plan first. Log: `[Rank] <rank> → <outcome>, ≈ $cost`.
- Typed tests beside a running Clicky: `CLICKY_DATA_DIR=<dir>` = own settings.json/log/mutex; put fake DPAPI keys there and point a copied build's `Endpoints.Anthropic` at a fake local server (scratch `fakeclaude.py`, `mkdata.ps1`). `bin/` reads are denied: build with `-o <scratch>`.
- Engineer (P3): `AgentController.RunEngineerAsync(task, request, preApproved, folder)` → `Services/ClaudeCodeBridge.cs` runs the user's own `claude.exe -p --output-format stream-json --verbose --permission-mode acceptEdits --allowedTools …` (folder = a `Desktop\Claude Code` subfolder named in the task, else that root; `--resume` within 30 min in the same folder; strips inherited `CLAUDE*`/`ANTHROPIC_*` env so the CLI uses its own plan login). Card banner = stream; spoken = start, first edit, build, 1–2 result sentences; hands-free "stop" kills the process tree. Test: `--claude "task" [--cwd folder]`. Policy (help center 2026-10-28): `claude -p` on a plan login draws from normal plan limits (the 2026-06-15 Agent SDK credit change is paused); monthly API credits only with a Console API key. Later: voice approval via `--permission-prompt-tool` (local MCP server).
- `Services/` also: `VoiceSession.cs` (one voice turn, hands ASR session over for reuse), `DictationManager.cs` (Dictate/Write/Prompt text + cleanup), `KeyboardHookService.cs`, `ElevenLabsService.cs`, `WindowsVoiceService.cs` (SAPI), `AudioOutput.cs`, `AudioDucker.cs`, `MicLevelMeter.cs`, `UserInputWatcher.cs`, `TeacherLesson.cs`, `ClaudeCodeBridge.cs`, `AccuracyReport.cs`.
- UI: `OverlayWindow` (dot + captions), `NotchWindow`, `QuickPanel`, `SetupWindow`, `HighlightWindow`, `SketchWindow`, `AgentCard`, `MainWindow` (tray). Root: `TaskSession.cs`, `AgentController.cs`. Also `Helpers/` (Win32, TextInserter, Logger, L, WordLearner...), `Models/`, `Settings/AppConfig.cs` + `AppSettings.cs`.
- `Helpers/L.cs`: ES/EN strings ("Auto" follows the language last spoken).

## Rules
- Crash mid hands-free session: ducked volumes are saved to `%APPDATA%\ClickyWindows\ducked.json` and restored at next start (and in the fatal-crash handler).
- Tests: no harness in the repo. Use a scratch console app that references bin\Release ClickyWindows.dll plus a fake AssemblyAI WebSocket (HttpListener). Don't run tests with real keys. Typed session test: `--session-say "a|b|end session"`.
- Never put API keys in chat, logs or tracked files; users enter them in the Setup window.
- UI updates from background threads: `Dispatcher.InvokeAsync`, never `Invoke`.
- Tests: scratch harness compiles Services/Helpers/Models/Settings against a fake local Claude SSE server.

## Open phases
See `docs/ROADMAP.md` (gap analysis vs mac Clicky/HeyClicky; P1 + D1–D4 + P3–P5 done 2026-10-14).
Launch flags: `--settings` opens Setup, `--panel` opens the tray panel (screenshots/tests), `--capturable` skips WDA_EXCLUDEFROMCAPTURE so screenshots/computer use can see the notch, overlay and cards (it also leaks them into Claude's screenshots; UI checks only).
Installer: `installer\build.ps1` → `dist\Clicky-<Version>-Setup.exe` (self-contained `Dist` profile + Inno Setup 6, dark wizard; version from csproj `<Version>`). `-Install` also upgrades the installed copy (%LOCALAPPDATA%\Programs\Clicky) in place; bump `<Version>` first.
UI: `Themes/Dark.xaml` = mac DesignSystem tokens (implicit styles for all windows); mac icon in `Assets/` (MIT, Farza; see THIRD_PARTY_NOTICES.txt). Overlay/Setup are excluded from or hard to screenshot: render XAML offscreen to check.
Release build: `dotnet publish -p:PublishProfile=Release` → `publish\` (R2R; no startup gain measured, ~1 s either way).
Overlay = one 320x200 window that moves with the dot (2026-10-15; idle 42 MB Task Manager / 67 MB private bytes, was 56/100). Remaining: multi-monitor (skipped by user).
