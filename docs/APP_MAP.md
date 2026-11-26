# ClickyWin app map (for a beginner, 2026-11-26)

How the code is laid out and what runs when you press a hotkey. Rules and detail: `CLAUDE.md`. Status and to-do: `docs/STATUS.md`. Plans: `docs/ROADMAP.md`, `docs/PLAN_RANKS_TEACHER.md`.

## 1. What it does
ClickyWin is a Windows voice assistant (C#, WPF, .NET 10), a port of the Clicky app. You hold a hotkey and talk.
It listens, looks at your screen, answers out loud, and points at things with a blue dot.
Other hotkeys type what you say into any text box (Dictate, Write, Prompt). It can also run tasks on the PC for you, after you approve.

## 2. The big picture

```
 You hold Ask (Ctrl+Shift+Space)  /  Dictate (Ctrl+Win)  /  Write (Alt+Win)  /  Prompt (Shift+Win)
        |
        v
 Services/KeyboardHookService.cs      <- sees the keys system-wide
        |
        v
 Services/CompanionManager.cs         <- one "session" per press, runs the steps below
        |  mic             |  words                 |  screenshots
        v                  v                        v
 AudioCaptureService   AssemblyAIService      ScreenCaptureService
 (record 16k audio)    (speech to text)       (pictures for Claude)
                           |
                           v
                    Services/ClaudeService.cs   <- Claude answers (fast model first, bigger one if needed)
                           |
          +----------------+-------------------+
          v                v                   v
   SpeechService      PointRefiner        reply ends with [DO: task]
   (ElevenLabs or     (blue dot lands     -> AgentController.cs -> Allow card
    Windows voice)     on the real button)    -> Services/ComputerAgent.cs -> Helpers/InputDriver.cs (mouse/keys)
```
Dictate / Write / Prompt skip the screenshots and the voice: `Services/DictationManager.cs` types the text at the caret (`Helpers/TextInserter.cs`).

## 3. Where each part lives
Root:
- `App.xaml.cs`: startup, single-instance, launch flags, tray. `MainWindow.xaml.cs`: the tray icon window.
- `AgentController.cs`: approval card, countdown, routes a task to a rank (runner / teacher / worker / engineer).
- `TaskSession.cs`: hands-free session (double-tap Ask, mic stays open, each spoken turn is a command).
- Windows: `OverlayWindow.xaml.cs` (dot + captions), `NotchWindow.xaml.cs` (black pill at screen top), `QuickPanel.xaml.cs` (tray panel), `SetupWindow.xaml.cs` (settings and keys), `HighlightWindow.xaml.cs` (glow), `SketchWindow.xaml.cs`, `AgentCard.xaml.cs` (approve/confirm/banner).
- `Themes/Dark.xaml`: dark look for all windows. `Assets/`: icons.

`Services/` (the work):
- `Services/CompanionManager.cs`, `Services/VoiceSession.cs`: one press / one voice turn.
- `Services/ClaudeService.cs`: Anthropic calls, parses `[POINT]` and `[DO:]` tags. `Services/ClaudeCodeBridge.cs`: runs your own Claude Code for coding tasks.
- `Services/AudioCaptureService.cs`, `Services/AudioOutput.cs`, `Services/AudioDucker.cs` (lowers other sound), `Services/MicLevelMeter.cs`.
- `Services/AssemblyAIService.cs`: speech to text. `Services/SpeechService.cs`, `Services/ElevenLabsService.cs`, `Services/WindowsVoiceService.cs`: speech out.
- `Services/ScreenCaptureService.cs`, `Services/PointRefiner.cs`: screenshots and dot placement.
- `Services/ComputerAgent.cs`, `Services/Runner.cs`, `Services/TeacherLesson.cs`, `Services/UserInputWatcher.cs` (stops a task when you touch mouse/keys/Esc).
- `Services/DictationManager.cs`, `Services/KeyboardHookService.cs`, `Services/AccuracyReport.cs` (dictation accuracy report).

Helpers and data shapes:
- `Helpers/`: `Helpers/Win32.cs`, `Helpers/InputDriver.cs`, `Helpers/TextInserter.cs`, `Helpers/Logger.cs`, `Helpers/L.cs` (Spanish/English text), `Helpers/WordLearner.cs`, `Helpers/HotkeyParser.cs`, `Helpers/FocusReader.cs`, `Helpers/Autostart.cs`, `Helpers/CoordinateHelper.cs`, `Helpers/SentenceSplitter.cs`, `Helpers/DarkMenu.cs`, `Helpers/MacChrome.cs`.
- `Models/AppState.cs`, `Models/ConversationHistory.cs`. `Settings/AppConfig.cs`, `Settings/AppSettings.cs`: read the two settings files.

## 4. Where data and settings live
- `appsettings.json` (ships next to the exe): prompts, hotkeys, endpoints, model names, screenshot limits.
- `%APPDATA%\ClickyWindows\settings.json`: your choices and API keys (keys encrypted with Windows DPAPI). Written by the Setup window. Example shape: `settings.example.json`.
- Also in `%APPDATA%\ClickyWindows\`: the log (tray menu opens it), `%APPDATA%\ClickyWindows\ducked.json` (saved volumes, restored after a crash), `%APPDATA%\ClickyWindows\recordings` (only if "Keep dictation recordings" is on).
- Env var `CLICKY_DATA_DIR`: use another folder for settings, log and lock (for tests next to a running Clicky).
- Keys are typed into the Setup window only. Never put them in chat, logs or tracked files.
- Installed copy: `%LOCALAPPDATA%\Programs\Clicky`.

## 5. How to run
- Normal: start Clicky from the Start menu (installed copy), or build and run `ClickyWindows.csproj` with the .NET 10 SDK (`global.json` pins the SDK).
- Flags: `--settings` opens Setup, `--panel` opens the tray panel, `--capturable` shows the notch/cards in screenshots (UI checks only).
- Test without hotkeys: `--ask "question"`, `--do "task"`, `--session-say "line|end session"`, `--claude "task"` (see `CLAUDE.md`).
- Installer (only to ship): `installer\build.ps1`, uses `installer/Clicky.iss`; version comes from `<Version>` in `ClickyWindows.csproj`.

## 6. How to check a change
No automatic check (no test project). Build it, then try the hotkey. The test ideas in `CLAUDE.md` use a scratch fake server, not real keys.

## 7. What breaks most easily
- Hotkeys: the low-level keyboard hook in `Services/KeyboardHookService.cs` and `Helpers/HotkeyParser.cs`. Another app using the same keys will clash.
- Speech to text: `Services/AssemblyAIService.cs` (session reuse, timing, free-account limit of 5 new sessions per minute). Status notes list live checks still open.
- Dot position: `Services/ScreenCaptureService.cs` keeps images under Claude's size limits so dot coordinates map back exactly. Change image size and the dot drifts.
- Computer use: `Services/ComputerAgent.cs`, `Helpers/InputDriver.cs`, `AgentController.cs` move your real mouse and keys. Keep the approval card.
- Updating the screen from a background thread: use `Dispatcher.InvokeAsync`, never `Invoke`.
- The tag `v0.0-upstream` is the original port, not a rollback point for this work.

Undo one file: `git checkout org-2026-11-25 -- <file>`. Undo a commit: `git revert <commit>`. Tag `org-2026-11-25` is the rollback point.
