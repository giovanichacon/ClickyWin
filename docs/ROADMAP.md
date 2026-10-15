# ClickyWin — gap analysis and dictation plan (2026-10-14)

**Status 2026-10-14:** P1, D1–D4, P3 (panel, copy answer, autostart, voice speed), P4 (streaming TTS),
P5 (R2R publish profile) and tutor mode are implemented. Not done: P2 overlay rewrite / multi-monitor
(user skipped multi-monitor). 2026-10-15: P2 overlay done as one small window that moves with the dot (idle private bytes 100 → 67 MB). Added later: Prompt mode (Shift+Win) and precise pointing (PointRefiner:
UI Automation name match, else full-resolution zoom crop; fixed a taskbar icon miss of ~550 px). Measured idle: ~1 s to ready, ~84 MB private (full-screen overlay is the
biggest remaining cost).

Sources: farzaa/clicky README (open-source macOS app, Apr 2026), heyclicky.com/changelog
(closed-source successor "HeyClicky", v1.0.11–v1.0.53), Farza's dictation launch post (Jul 2026),
AssemblyAI streaming docs.

## 1. Original open-source Clicky (macOS) vs ClickyWin

| Area | Mac original | ClickyWin | Gap |
|---|---|---|---|
| Push-to-talk | Ctrl+Option, modifier-only | Any Ctrl/Alt/Win + key, recorder in Settings | Modifier-only and double-tap need a low-level hook (P1) |
| Home UI | Menu-bar panel, no focus steal | Tray menu + Setup window | Quick panel: status, last Q/A, model, mic (P3) |
| Cursor buddy | Spring dot, waveform, spinner, pointing labels | Same; hidden when idle (option) | Overlay covers only the primary monitor (P2) |
| Multi-monitor pointing | `[POINT:x,y:label:screenN]` | Parsed + mapped | Not drawn on screen 2+ until P2 |
| Screenshots | ScreenCaptureKit, own windows excluded | GDI, overlay excluded (WDA_EXCLUDEFROMCAPTURE) | — |
| STT | AssemblyAI streaming (token via worker) | AssemblyAI v3 direct, pre-roll, warm mic | — |
| LLM | Claude (Sonnet/Opus picker) | Haiku 5.5 → [ESCALATE] → Opus 5.5, cost log | — |
| TTS | ElevenLabs | ElevenLabs + voice picker, Windows SAPI fallback | Streaming / sentence TTS (P4) |
| Key handling | Cloudflare Worker proxy | DPAPI-encrypted local keys | Not needed for personal use |
| Copy answer to clipboard | Option | — | Quick win (P3) |
| Tutor mode (proactive guide) | Yes | — | Later, token-heavy |

## 2. What HeyClicky added since (closed source)

- Dictation (v1.0.37, Jul 2026): hold Fn+Ctrl → fast STT (~450 ms) + a second model for "faithful cleanup"
  (no added words, no em dashes) → typed into the focused field (not clipboard), incl. Chromium/Electron.
  Double-tap = hands-free, cancel key, 50+ languages, personal dictionary that auto-learns words you
  correct within ~20 s.
- Screen-aware dictation: hold Ctrl+Option, say the intent ("reply that I'll be there at 5") → screenshot
  + intent → it writes the text in your voice (Gmail reply, Claude Code prompt) and inserts it.
- Also: computer use (clicks), Google/Notion/Linear/MCP connectors, memory files, notch UI, realtime voice,
  voice speed 0.5–1.5×, auto fast/frontier routing, "hide floating cursor", double-tap shortcuts.

## 3. Plan

P1 Keyboard hook — `WH_KEYBOARD_LL` on its own thread (replaces RegisterHotKey). Multiple bindings
   (Ask, Dictate, Write), modifier-only holds (Ctrl+Win), double-tap toggle, Esc cancels. Prerequisite for dictation.

D1 Dictation MVP — new `DictationManager` beside `CompanionManager`, reusing `AudioCaptureService` +
   `AssemblyAIService`. On press: remember foreground HWND + focused control. On release: ForceEndpoint,
   join turns, insert. Insert via `SendInput` + `KEYEVENTF_UNICODE` (works in Chromium/Electron/Win32);
   long text or failure → clipboard paste with save/restore. Small "listening" pill at the caret
   (GetGUIThreadInfo / UI Automation caret rect). Note: Windows blocks input into admin windows (UIPI).

D2 Cleanup — Haiku 5.5, thinking off, "faithful" prompt (punctuation, casing, drop fillers, apply
   self-corrections "no wait, 5pm", never add content). Toggle Raw/Clean. Personal dictionary →
   AssemblyAI `keyterms_prompt` (≤100 terms, ≤50 chars, updatable mid-stream) + cleanup prompt.
   Cost ≈ $0.0001 per dictation on Haiku.

D3 Screen-aware Write (our twist) — besides the screenshot, read the focused field and selection with
   UI Automation (TextPattern/ValuePattern) + process name (Outlook, VS Code, Windows Terminal…). Text is
   exact and costs fewer tokens than OCR from pixels; the screenshot stays for layout. Haiku writes,
   same [ESCALATE] rule. Selected text + "make it shorter" = rewrite in place.

D4 Polish — auto-learn dictionary (watch the field via UIA for 20 s after insert, diff words),
   hands-free double-tap, dictation history in tray (re-copy last 20), per-app style hints.

Also queued: P2 overlay per monitor, P3 tray quick panel + copy answer + start with Windows +
voice speed, P4 ElevenLabs streaming TTS, P5 release build (R2R, RAM/startup).
