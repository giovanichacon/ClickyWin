# ClickyWin — Windows port of farzaa/clicky (C# WPF, .NET 10)

Hold hotkey → mic (WASAPI) → AssemblyAI v3 streaming → Claude (screenshots) → voice + blue dot pointing.
Base: emreyilmaz46/clicky_windows (MIT), remote `upstream`. Work on branch `dev`; tag `v0.0-upstream` = original.

## Layout
- `appsettings.json` (shipped, next to exe): prompts, hotkey, endpoints, models, screenshot limits.
- `%APPDATA%\ClickyWindows\settings.json`: user choices + API keys (DPAPI-encrypted), written by `SetupWindow`.
- `Services/CompanionManager.cs`: per-press Session, state machine, pipeline on thread pool.
- `Services/ClaudeService.cs`: official Anthropic SDK; Haiku 5.5 first (thinking off), `[ESCALATE]` → Opus 5.5 (fallbacks "default").
- `Services/AudioCaptureService.cs`: one reused WASAPI client (fast restart), 100 ms PCM16/16k frames.
- `Services/AssemblyAIService.cs`: single send loop (pre-roll), ForceEndpoint + Terminate, all turns joined.
- `Services/SpeechService.cs`: ElevenLabs, falls back to Windows SAPI voices.
- `Services/ScreenCaptureService.cs`: images kept under Claude no-resize limits so [POINT] coords map back exactly.
- `Helpers/L.cs`: ES/EN strings ("Auto" follows the language last spoken).

## Rules
- Never put API keys in chat, logs or tracked files; users enter them in the Setup window.
- UI updates from background threads: `Dispatcher.InvokeAsync`, never `Invoke`.
- Tests: scratch harness compiles Services/Helpers/Models/Settings against a fake local Claude SSE server.

## Open phases
2 overlay (full-screen layered window → small moving windows, CompositionTarget.Rendering, per-monitor DPI),
3 hotkey (WH_KEYBOARD_LL on own thread), 6 ElevenLabs streaming, 7 release build (R2R, measure RAM/startup).
