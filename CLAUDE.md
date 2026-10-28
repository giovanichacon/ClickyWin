# ClickyWin — Windows port of farzaa/clicky (C# WPF, .NET 10)

## Next-session to-do (2026-10-28)
- NEXT TASK (user asked): research the current Mac Clicky (farzaa/clicky, HeyClicky) on performance and features, then give a plan to reach parity. Compare against `docs/ROADMAP.md` (older gap analysis) and the state below. Plan only, then wait for the user's pick.
- Branch: work is on `main` (ahead of origin/main, not pushed); `dev` stopped at 1.1.1. Installed copy = 1.1.1. User said don't ship yet. To ship: bump `<Version>` to 1.1.2, `installer\build.ps1 -Install`, tag release-1.1.2.
- Unshipped on `main`: ff565c6 hands-free ducking + no-Termination-wait; 7401616 ASR `language_codes` ["en","es"] + context `prompt` (`Speech.AssemblyAiLanguages/AssemblyAiPrompt` in appsettings.json); 5ee14ba Dictate skips the Haiku cleanup unless `DictationManager.NeedsCleanup` regex matches (fillers, "no,", "new line"…); 0ee8426 accuracy report + fix for the feedback bubble stuck at the cursor.
- Real-mic results 2026-10-28: release-to-typed ~0.35 s when cleanup is skipped (user: "very fast, keep it"), ~1.4 s when it runs. EN+ES mixing OK. Errors left: "Clicky" → "Kiki", "Wispr Flow" → "WhisperFlow", one 3.4 s clip came back empty. User only dictates EN + ES (Polish would need `whisper-rt`). Hands-free ducking with music: not tested yet.
- Accuracy report (0ee8426, `Services/AccuracyReport.cs`): Setup checkbox "Keep dictation recordings" (`KeepRecordings`, off by default) saves WAV + `.heard.txt` in `%APPDATA%\ClickyWindows\recordings` (newest 100, not hands-free). Setup "Accuracy report" button uploads new clips to AssemblyAI pre-recorded (`/v2/upload`, `/v2/transcript`, `language_detection`), writes `.ref.txt`, opens `report.html` (word diff, top mix-ups). Diff checked offline; the real API path has never run. Pending: user enables it, dictates, runs report → read the `.heard.txt`/`.ref.txt` pairs and turn the mix-ups into dictionary terms / prompt tweaks.
- Decisions: learn-from-corrections stays manual (existing `LearnWords`, fix the word in the box); user rejected spoken "I said X, not Y" learning. User wants Wispr-Flow-level dictation accuracy. Suggest adding `Clicky`, `Wispr Flow` to the personal dictionary.
- Notch (2026-10-28, HeyClicky-style): `NotchWindow` = black pill at the top center of the primary screen, showing a status dot and text that follow companion/dictation state, plus a gear button that opens Setup. Clicking it toggles QuickPanel below it (`ShowBelow`). Setting `ShowNotch` (default on) is in the General tab. It is capture-excluded and non-activating. It has been checked offscreen only, not live in the running app yet. Next UI ideas: U3 chat in the panel, U1 settings redesign.
- Crash mid hands-free session: ducked volumes are saved to `%APPDATA%\ClickyWindows\ducked.json` and restored at next start (and in the fatal-crash handler).
- Tests: no harness in the repo. Use a scratch console app that references bin\Release ClickyWindows.dll plus a fake AssemblyAI WebSocket (HttpListener). Don't run tests with the user's real keys (that was denied). Typed session test: `--session-say "a|b|end session"`.

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
