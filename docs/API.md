# Maid's API

Maid, the dashboard that runs the user's Claude Code sessions, can be controlled by programs: a Claude
Code session (through `maidctl`) and any other program of the user's at the PC. With it they list the projects and
the windows, start, stop and close sessions, type into them, read their screens, and give a session a prompt and
wait for its answer.

The way in is the local named pipe `\\.\pipe\Maid.Api` (`settings.apiPipe`, empty = off; read at start). Only the
user's own programs get through, and only elevated ones while Maid runs elevated (as it does by default). Nothing
from the network.

## maidctl (at the PC)

`maidctl.exe` is in `cli\` next to `Maid.exe`, and Maid puts that folder first on the PATH of every session it
starts. Every session also gets `MAID_WINDOW` (the id of its own window) and `MAID_API_PIPE`. From another shell
use the full path, e.g. `C:\Program Files\Maid\cli\maidctl.exe`. The shell must be elevated, because
Maid is.

```
maidctl status                       Maid and its windows (how many work, ask)
maidctl projects                     the listed projects by group, with their folders
maidctl windows                      the open windows: name, state, program, id, last answer
maidctl screen <window> [--lines N]  the text a window shows (or its last N lines, scrollback included)
maidctl answer <window>              a window's last answer
maidctl launch <project> [--resume] [--prompt <text>|-]
maidctl send <window|project> <text>|-
maidctl ask <window|project> <text>|- [--timeout S]
maidctl wait <window> [--timeout S]
maidctl type <window> <text>|- [--enter]
maidctl keys <window> <key>...
maidctl stop | close | show <window>
maidctl relaunch <window> [--resume]
maidctl add <folder> [--group <name>]
maidctl raw '<json request>'
```

- **Windows:**
  - Name a window by its name as Maid shows it (`MyApp`, `MyApp #2`) or by its id.
  - `self` is the caller's own window and `jarvis` is the voice tile.
  - A project's name also works when that project has one window.
  - A name that fits several windows is refused, with the candidates listed.
- **Projects:**
  - Name a project by its name, a unique part of it (letters and digits only, so spoken names work), its folder, or
    `hub` for All projects.
  - `send`/`ask` to a project goes to one of its Claude Code sessions that waits for a prompt. If none waits, a new
    session starts with the prompt as its first prompt: an idle session gets the prompt typed in, and a busy one is
    left alone.
- **Text:** a text of `-` is read from standard input. Use that for long or multi-line prompts (at most 16,000
  characters).
- **Waiting:**
  - `ask` and `wait` wait 110 s by default, which fits Claude Code's default Bash timeout. Raise `--timeout` (at most
    3600) together with the shell command's own timeout.
  - When the time is up the exit code is 3, and `maidctl wait <window>` carries on.
  - The answer goes to standard output, and how the turn ended (still working, asking the user, ended) to standard
    error.
- **Exit codes:** 0 done, 1 refused or failed, 2 wrong arguments, 3 still working, 4 Maid not reachable.
- **Keys:** `enter esc tab shift+tab up down left right home end pageup pagedown backspace delete space ctrl+a … ctrl+z`,
  or a single character (`y`, `1`). They are pressed one at a time.
- **Prompts vs. typing:** a session must never `ask` or `wait` for itself; Maid refuses that. `type` is for answering
  menus and command lines. Prompts go with `send`/`ask`, because only those wait for the turn the prompt starts.

## States

A window's `state`:

- `starting`
- `working`: a turn runs, or a prompt it was given hasn't started its turn yet.
- `asking`: it waits for the user's answer to a question (`AskUserQuestion`, `ExitPlanMode`) or a permission prompt.
  `question` then holds the details.
- `idle`: it waits for the next prompt.
- `ended`
- `failed`: the program could not start.

For Claude Code these come from its hooks, and `answer` is the `Stop` hook's `last_assistant_message`. A session
without hooks (a `command` other than Claude Code) has its state guessed from its output, reports no answer
(`hooks: false`), and its reply is read with `screen`.

`wait` (and `ask`) answers once the turn is over: done (`idle`), `asking`, `interrupted` (Esc), `ended`, or
`timedOut` while still working. A prompt sent while a turn runs waits in Claude Code's queue, and the wait then
ends with the answer of the turn that prompt starts, not of the one that was running.

## The protocol (pipe)

One JSON object per line, in both directions. A request is `{ "id": "<yours>", "op": "<op>", …arguments,
"caller"?: "<MAID_WINDOW>" }`. Its answer is `{ "id", "ok": true, "result": {…} }` or `{ "id", "ok": false,
"error": "<a sentence>" }`. Several requests can be in flight on one connection; answers come back by id.

| op | arguments | result |
|---|---|---|
| `hello` | | `app, version, api (1), elevated, window` |
| `status` | | `version, elevated, projects, windows { open, running, working, asking }, hub` |
| `projects` | | `projects [{ id, name, path, group (e.g. "Work / Clients"), windows, running, moving }], groups [{ name, path, parent, color }], hub { id, name, path, running }` |
| `windows` | | `windows [{ id, name, project, projectId, path, group, jarvis, state, title, message, answer (≤400 chars), answeredAt, question, hooks }]` |
| `window` | `window` | one window as above, with its whole `answer` |
| `launch` | `project`, `resume`, `prompt` | `window, project, launched` |
| `send` | `text` and `to` (window or project) / `window` / `project` | `window, launched` |
| `ask` | as `send`, plus `timeout` (s, default 600, max 3600) | as `wait`, plus `launched` |
| `wait` | `window`, `timeout` (0 = just look) | `window, name, state, answer, answeredAt, question, failure, interrupted, timedOut, exitCode, hooks` |
| `type` | `window`, `text`, `enter` | `window` |
| `keys` | `window`, `keys` (array, or a string split at spaces) | `window, pressed` |
| `screen` | `window`, `lines` (0 = the screen) | `window, name, state, text, cols, rows` |
| `stop`, `close`, `show` | `window` | `window, name` |
| `relaunch` | `window`, `resume` | `window, name` |
| `addProject` | `path`, `group` | `project { id, name, path, group }, added` |

Sessions started through the API never take the keyboard from whoever types at the PC (`show` brings a window to
the front on purpose). From PowerShell, without maidctl:

```powershell
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'Maid.Api', 'InOut'); $pipe.Connect(3000)
$writer = [System.IO.StreamWriter]::new($pipe); $reader = [System.IO.StreamReader]::new($pipe)
$writer.WriteLine('{"id":"1","op":"windows"}'); $writer.Flush(); $reader.ReadLine()
```

## The voice pipe

A push-to-talk program at the PC hands spoken requests to the voice window in All projects (the tile marked
*Jarvis*) through `\\.\pipe\Maid.Voice` (`settings.voicePipe`, empty = off; read at start), guarded like the API's
pipe. One JSON object per line: `{ "type": "voice", "ref": "<yours>", "text": "<the request>" }` is answered with
`voiceSent { ref, id }` once it is in the voice window, then `voiceReply { ref, text }` (Claude's answer) or
`voiceFailed { ref, message }`. `{ "type": "hello" }` is answered with `hello { app, version }`.

