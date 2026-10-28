# Plan: paced captions, Teacher mode, ranks, Claude Code bridge (2026-10-28)

Goal: do real work by talking and watching the screen, and learn from it (Blender camera setup is the
reference case), while spending the fewest tokens. Build in phase order; each phase ships on its own,
commit per step.

## P0 Captions paced with the voice (small, first)
Problem: `SpeechService` fires `Caption` when a sentence is *requested*. ElevenLabs downloads faster than
real time (`ElevenLabsService.Playback.AppendAsync` fills a 5-min `BufferedWaveProvider`), so every caption
lands before its audio and the box shows the whole reply at once.
- `Playback`: record each sentence's start byte (total bytes appended before it). Played position =
  total appended - `BufferedBytes`. A timer (50 ms) fires the caption when playback reaches that byte.
- Inside a sentence, reveal words progressively: sentence duration = its byte count / 48000 B/s
  (pcm_24000 16-bit), spread words by character share. No timestamps endpoint needed.
- Windows voice: already per sentence; add the same word reveal using SAPI `SpeakProgress` events.
- Caption box keeps the last ~2 lines (rolling), not the whole reply.
Done when: captions never run ahead of audio by more than ~150 ms, with both voices.

## P1 Teacher mode (interactive, visual)
Builds on the existing Tutor mode (`CompanionManager` 348+: watches thumbnails, asks the next step,
points with [POINT]). New pieces:
1. **Step loop by voice**: each step is said + shown, then Clicky waits. Commands: "next", "again",
   "show me", "let me try", "slower", "why?", "stop". Hands-free session routes these words to the
   lesson first (like `card.TryAnswer`).
2. **Show me / let me try**:
   - *Show me*: Clicky does the step itself with the visible cursor, moving slowly (eased moves,
     pauses before clicks), saying what and why *before* acting. Uses `InputDriver` (agent stack).
   - *Let me try*: Clicky first undoes its demo where possible (Ctrl+Z), then draws the gesture and
     watches you do it (thumbnail diff + one check), and says "good" or corrects you.
3. **Drawing on screen** (overlay, capture-excluded): new reply tags
   - `[DRAG:x1,y1>x2,y2 button=middle mods=shift]`: animated trace along the path with a mouse icon
     showing the button (orbit/pan/zoom in Blender).
   - `[KEYS:Ctrl+Alt+Num0]`: key-cap chips near the cursor (e.g. "align camera to view").
   - `[CIRCLE:x,y,r]` / existing `[POINT]`: highlight a control.
   All coords use the same screenshot mapping as [POINT].
4. **Models**: Sonnet for steps (needs exact vision), Haiku for "is the step done yet?" checks.
   Cost cap per lesson (default $0.50).
Done when: "show me how to set up the camera in Blender" runs as a paced lesson: draws the orbit
gesture, shows key chips, demos on request, and checks your attempt.

## P2 Ranks (route each request to the cheapest worker)
| Rank | Who | For |
|---|---|---|
| Receptionist | Haiku chat (existing Ask reply) | understands, answers, picks the rank |
| Runner | no model / Haiku agent | mundane: play a song, open a site, a setting |
| Teacher | P1 | "show me / teach me" |
| Worker | Sonnet agent | do a multi-step screen task for me |
| Engineer | Claude Code (P3) | code, files, scripts, repos |
| Boss | Opus | plans long tasks once; advises when stuck; never drives the screen |
- Receptionist tags: `[DO:runner|teacher|worker|engineer: task]` (extends `ParseDoTask`).
- Runner shortcuts with zero agent tokens: YouTube = open `youtube.com/results?search_query=…`, then
  one Haiku agent click on the first result; known apps via `Process.Start`.
- Ladder: Haiku agent `hand_off` -> Sonnet (exists, goes to Opus today; change target to Sonnet).
  Sonnet gets a `consult` tool: one Opus call with the screenshot + its question, returns advice text;
  Sonnet keeps the screen. Long tasks (receptionist marks `long`): one Opus planning call first,
  the plan goes into the worker's prompt.
- Log the rank and cost per request so the routing can be tuned from real use.

## P3 Engineer = Claude Code bridge
- Run the user's own installed CLI: `claude -p "<task>" --output-format stream-json` in the project
  folder named by voice ("in ClickyWin" -> `Desktop\Claude Code\ClickyWin`). Uses the subscription's
  Agent SDK credit (since 2026-06-15, per news reports; confirm in the Claude Help Center first).
  Clicky never reads or stores Claude tokens.
- Voice: speak only milestones and the final result, not every line. Show the stream in the card.
- Permissions: start with `--permission-mode acceptEdits` plus an allowlist; later a voice yes/no via
  `--permission-prompt-tool` (needs a small local MCP server in Clicky).
- Cost: each `claude -p` call loads its system prompt + CLAUDE.md (tens of thousands of tokens), so
  only the Engineer rank uses it; continue a conversation with `--resume <session>` instead of restarting.

## Open questions (ask before P1/P3)
- Teacher demos: always undo before "let me try", or only when asked?
- Engineer: allow file edits without asking, or voice-confirm each one?
- Order after P0: P1 then P2 (recommended), or P2 first?
