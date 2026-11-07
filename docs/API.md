# Maid's API

Maid, the dashboard that runs the user's Claude Code sessions, can be controlled by programs: a Claude
Code session (through `maidctl`), any other program of the user's at the PC, and a voice assistant app on the
phone (through the Maid phone app). With it they list the projects and the windows, start, stop and close sessions, type into them,
read their screens, and give a session a prompt and wait for its answer.

There are two ways in, and both are ways Maid already had:

| Who | Way in | Who gets through |
|---|---|---|
| Programs at the PC (`maidctl`, scripts) | the local named pipe `\\.\pipe\Maid.Api` (`settings.apiPipe`, empty = off; read at start) | only the user's own programs, and only elevated ones while Maid runs elevated (as it does by default). Nothing from the network. |
| A voice assistant app on the phone | the Maid phone app's `JarvisRelay` (Android `Messenger`), then the app's existing link to Maid | only the one app the phone app was built for, checked by its package and signing certificate (none by default); then only the paired phone, over Tailscale and TLS, as always |

The phone may do what its buttons do. It can't change Maid's list of projects (`addProject` is refused).

## maidctl (at the PC)

`maidctl.exe` is in `cli\` next to `Maid.exe`, and Maid puts that folder first on the PATH of every session it
starts. Every session also gets `MAID_WINDOW` (the id of its own window) and `MAID_API_PIPE`. From another shell
use the full path, e.g. `C:\Program Files\Maid\cli\maidctl.exe`. The shell must be elevated, because
Maid is.

```
maidctl status                       Maid, its windows (how many work, ask), the phone
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
| `status` | | `version, elevated, projects, windows { open, running, working, asking }, phone { enabled, paired, connected, viewing }, hub` |
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
| `addProject` | `path`, `group` (not from the phone) | `project { id, name, path, group }, added` |

Sessions started through the API never take the keyboard from whoever types at the PC (`show` brings a window to
the front on purpose). From PowerShell, without maidctl:

```powershell
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'Maid.Api', 'InOut'); $pipe.Connect(3000)
$writer = [System.IO.StreamWriter]::new($pipe); $reader = [System.IO.StreamReader]::new($pipe)
$writer.WriteLine('{"id":"1","op":"windows"}'); $writer.Flush(); $reader.ReadLine()
```

## The voice pipe (at the PC)

A push-to-talk program at the PC hands spoken requests to the voice window through `\\.\pipe\Maid.Voice`
(`settings.voicePipe`, empty = off), guarded like the API's pipe. One JSON object per line: `{ "type": "voice",
"ref": "<yours>", "text": "<the request>" }` is answered with `voiceSent { ref, id }` once it is in the voice window,
then `voiceReply { ref, text }` (Claude's answer) or `voiceFailed { ref, message }`. `{ "type": "hello" }` is answered
with `hello { app, version }`.

## The phone app's API (for a voice assistant)

A voice assistant app on the phone binds the Maid phone app's service `com.xpnemec.maid/.JarvisRelay`. The relay lets
in only the app the phone app was built for. Build the phone app with
`-Pmaid.voiceApp.package=<that app's package> -Pmaid.voiceApp.certificate=<SHA-256 of its signing certificate, hex>`
(or put both in `~/.gradle/gradle.properties`); without them it lets no app in. The assistant talks to the relay with
`android.os.Messenger`, the data in each message's `Bundle`:

- **Voice request:** `what = 1` (`MSG_SEND`), with `replyTo` set, data `{ "ref", "text" }`: the text goes into the
  voice window in All projects. Answered with `what = 2` (`MSG_SENT`) `{ ref }`, then `3` (`MSG_REPLY`)
  `{ ref, text }` with Claude's answer, or `4` (`MSG_FAILED`) `{ ref, message }`.

- **Request:** `what = 5` (`MSG_API`), with `replyTo` set, data `{ "ref": "<yours, ≤64 chars>", "request": "<JSON
  text>" }`.
- **Answer:** `what = 6` (`MSG_API_RESULT`), data `{ "ref", "result": "<JSON text>" }`. The result is `{ "ok": true,
  "result": {…} }` or `{ "ok": false, "error": "…" }`, exactly one per request.

The requests are those of the table above; the app sends them over its link to Maid and hands back the answer. A
few are the app's own:

| op | arguments | result |
|---|---|---|
| `phone` | | `app, version, paired, link (connected/connecting/offline/…), computer, visible, showing (window id), showingName` |
| `show` | `window` | `window, name, shown`. The app comes up showing that window (Maid resolves the name). |
| `open` | | `shown`. The app comes up. |

Some practical points for a voice assistant:

- **Connecting:** a request connects the app to Maid when it is not connected, waiting up to 12 s. It fails if the
  app isn't paired or the PC can't be reached.
- **Timing:** `ask`/`wait` answer when the turn is over or after `timeout` seconds (default 600). Give spoken
  requests a timeout that suits them, e.g. 120, and when `timedOut` comes back, say the session is still working.
- **Bringing the app up:** Android lets an app in the background come to the front only in certain cases. When
  Maid's app can't, `show`/`open` fail with an error that names the activity to start. The assistant can then start
  `com.xpnemec.maid/.MainActivity` itself, with `FLAG_ACTIVITY_NEW_TASK` and the string extra `tile` = the window id;
  an accessibility service's app may. Alternatively, the assistant binds the relay with `Context.BIND_ALLOW_ACTIVITY_STARTS`
  (Android 14+) while it is visible.
- **Wording:** voice-friendly names work as they are heard. "my app" finds `MyApp`, and
  `windows[].state` and `answer` are short enough to read out.
- **Testing:** debug builds of the Maid app have `DebugApiReceiver`, to try requests with adb:
  `adb shell am broadcast -n com.xpnemec.maid/.DebugApiReceiver --es ref r1 --es request '{"op":"windows"}'`, with
  answers in logcat under the tag `MaidApi`.
