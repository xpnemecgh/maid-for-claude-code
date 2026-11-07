# Maid

A master dashboard for Claude Code sessions on Windows. Add your project folders, press ▶ on one, and it
starts `claude --dangerously-skip-permissions` there — inside a real terminal that is embedded in the
dashboard. All sessions are visible side by side in one window. **?** at the top right (F1) shows new users
around.

## Install

Download `Maid-Setup-<version>.exe` from [Releases](https://github.com/xpnemecgh/maid-for-claude-code/releases) and
run it. It installs Maid into Program Files, self-contained (no .NET needed), with the WebView2 Runtime where it is
missing, and before installing it says what Maid does on the PC. Install [Claude Code](https://claude.com/claude-code)
separately and sign in to it once. The setup is not signed, so Windows SmartScreen warns about it (*More info* →
*Run anyway*). To build the setup yourself: `tools\build-installer.ps1` (needs the .NET 9 SDK and
[Inno Setup 6](https://jrsoftware.org/isinfo.php)) → `dist\Maid-Setup-<version>.exe`.

## Run

Requirements: Windows 10 1809+ / 11, [.NET 9 SDK](https://dotnet.microsoft.com/download), the WebView2 runtime
(part of Windows 11), and `claude` on your `PATH`.

```powershell
dotnet run --project src\Maid                                   # run from source
dotnet publish src\Maid\Maid.csproj -c Release -o dist\Maid     # build dist\Maid\Maid.exe (pin it to the taskbar)
```

## Use

- **Edit mode:** everything that changes the list — the add form, the group **+**, renaming, colouring and
  removing groups, the **⋯** and **✕** on projects, dragging, **Shared info** — only shows while **Edit**
  (top right, Ctrl+Shift+E) is on. It is off every time Maid starts, so day to day the list has just ▶ ↺ ■.
- **Add a project:** turn on **Edit**, type or paste a folder path (or **Browse…**), pick the group it goes
  under (leave the box empty for *Misc*, or type a new name to create a group), then **Add** / Enter. This
  only lists it.
- **Session list (left):** ▶ starts Claude Code in that project, ↺ starts it with `--continue` to resume the
  last conversation in that folder, ■ stops, ✕ removes the entry after a confirmation (a running session in
  it is stopped). Double-click also starts it.
- **All projects:** the toolbar button starts a Claude Code session that is about all listed projects at
  once — e.g. "add an instruction for all projects", or a change to make in every project. It runs in Maid's
  own folder (`%APPDATA%\Maid\Hub`, or `"hubPath"`), whose `CLAUDE.md` tells it its role and lists the
  projects. Its `shared-info.md` is the shared info (below): when it is saved, Maid writes the text into every
  project right away; editing the shared info in Maid updates the file.
- **Move a project's folder:** **⋯** → *Move folder…* (Edit mode) moves the folder with all its files to a new
  path (a folder that does not exist yet, or an empty one) and points the project there; on another drive
  the files are copied and the original deleted once the copy is complete. Claude Code's saved
  conversations of the project move along, so ↺ keeps working. Stop its sessions first.
- **Groups:** projects are listed under labels of your choice. Click a label to collapse or expand it (the
  header's dot keeps showing what the sessions inside are up to); hover it to rename it or remove it
  (its projects go back to *Misc*), or move it up or down; a group can also be dragged by its header to
  any position. The **+** in the list header makes a new group (it goes last). Projects saved before
  groups existed are in *Misc*.
- **Group colours:** the droplet on a group's header (Edit mode) gives the group a colour — one of the
  palette, any other, or none. The terminals of its projects then write in that colour: normal text in
  the colour itself, the greys Claude Code uses for its secondary text in darker shades of it. Colours
  that mean something (Claude's orange, diffs, syntax highlighting) stay as they are.
- **Shared info:** **Shared info** in the toolbar (Edit mode) opens an editor for one text that Maid keeps
  in the `CLAUDE.md` (and `AGENTS.md`) of every project in the list — in a marked section of its own, written as the
  Markdown you typed. Saving (Ctrl+Enter) rewrites that section in all projects; a project you add later
  gets it too, and each launch makes sure the project's file still carries the current text. Saving it
  empty takes the section out again (a `CLAUDE.md` with nothing else in it is deleted), as does removing a
  project from the list.
- **Names and order:** the **⋯** button on a project (hover) opens its menu: rename it (the name is yours
  to choose; clear it to go back to the folder's name), move it up or down, move it to another group or a
  new one, or remove it. Projects can also be dragged: within their group to reorder them, or onto another
  group's projects or header to move them there.
- **Administrator:** the consoles run as administrator. A session gets Maid's rights, so Maid restarts itself
  elevated when started without (Windows may ask to allow it); the title bar then reads *Administrator: Maid 1.2* (the number is Maid's version).
  If elevation is refused, Maid runs without and says so. Turn off with `"runAsAdmin": false`.
- **Trust prompt:** Claude Code's "do you trust this folder?" question is confirmed automatically when a
  session starts (you chose the folder by adding it). Turn off with `"autoTrust": false`.
- **AGENTS.md:** Codex reads `AGENTS.md` where Claude Code reads `CLAUDE.md`, so every listed project gets an
  `AGENTS.md` with the same text as its `CLAUDE.md`, and Maid keeps them the same: whichever of the two was
  edited (by you, Claude or Codex) is copied over the other — at start, when a session starts or stops, and
  whenever Maid writes its sections. A project that already had a different `AGENTS.md` of its own gets the
  `CLAUDE.md` text in it too; the old file is kept as `AGENTS.maid-original.md` (a `CLAUDE.md` that only says
  `@AGENTS.md` gets that file's text in its place instead). If both files were changed in the meantime, the
  later change wins and the other text is kept as `CLAUDE.maid-conflict.md` / `AGENTS.maid-conflict.md`.
- **Conversation history:** whenever a session stops — ■, closing its tile, closing Maid, or `/exit` — the
  conversation (your prompts and Claude's — or Codex's — replies, no tool calls) is saved into a marked section at the end
  of the project's `CLAUDE.md`, so the next session starts with that context. Saving again updates the same
  conversation instead of duplicating it; everything outside the markers is never touched. The section is
  capped (`"historyMaxChars"`, default 24000 — `CLAUDE.md` is loaded into every session) by dropping the
  oldest conversations first; the full transcripts stay in `~/.claude/projects`. Turn off with `"saveHistory": false`.
- **Tiles:** each open session is a tile; the grid arranges itself (or pick 1–4 columns). Double-click a tile
  header or press F11 to maximize it. When a session ends, its tile keeps the final output and offers
  Launch / Resume / Close.
- **Status dot:** orange pulsing = working (output is flowing), green = idle, green with a ring and a green
  tile border = *finished while you were looking elsewhere* (the taskbar button flashes too if Maid is in
  the background), hollow = not running, red = exited with an error.
- Stopping or closing a **running** session asks for a second click; closing Maid with running sessions asks too.

| Shortcut | |
|---|---|
| Ctrl+Tab / Ctrl+Shift+Tab | next / previous session |
| Ctrl+1 … Ctrl+9 | jump to session N |
| F11 | maximize / restore the active session |
| Ctrl+Shift+E | edit mode on / off |
| Ctrl+Shift+N | edit mode on and focus the path box |
| Ctrl+Shift+B | show / hide the session list |
| Ctrl+= / Ctrl+- / Ctrl+0 | terminal font size |
| Ctrl+C | copy if text is selected, otherwise interrupt (as in Windows Terminal) |
| Ctrl+V, Ctrl+Shift+V, right click | paste (right click copies when there is a selection) |
| Shift+Enter | newline in Claude's prompt |

## Phone app (Android)

`android/` is an Android app that shows Maid's windows on your phone, one at a time: a strip of the PC's
windows on top (swipe the terminal sideways or tap a window to switch), the terminal, a row of keys (Esc,
Tab, ⇧Tab, arrows, ⌫, ^C, ⏎, more under ⋯) and a message box (*Send* types the text and presses Enter;
hold it to send without Enter). The microphone next to it dictates: tap it to start and again to stop, or
hold it while you talk. Maid on the PC has ElevenLabs write the recording out (in many languages, mixed
ones too), and the words land in the message box for a look before you send them; press *Send* while
still recording to send them straight away. The grid button lists every window and every project, to
start Claude Code or a resumed conversation from the phone; the ⋮ menu stops or closes a window.
It shows which sessions work and which wait for you, without notifications, and locks itself with your
fingerprint / screen lock. "Working" and "Waiting for you" come from Claude Code's own hooks, which Maid
gives each session it starts, so a session that is quiet while it works is not taken for one that waits.

Voice: a voice assistant app on the phone can pass what you say after "On computer, …" through this app to a
voice window of its own in All projects (the tile marked *Jarvis*) and read Claude's answer to you. The phone app
lets in only the one app it was built for (see [docs/API.md](docs/API.md)). At the PC, a push-to-talk program can do
the same through a local pipe that only your own programs running as administrator can open (never the network). The voice window runs
Claude Code with `--model opus --effort xhigh` (`"voiceArguments"`).

Setup:

1. Install [Tailscale](https://tailscale.com) on the PC and the phone, signed in with the same account.
2. Build the app (below) and install `android\dist\Maid.apk` on the phone (e.g. `tailscale file cp
   android\dist\Maid.apk <phone>:` and open it from Tailscale's notification, or copy it over USB).
3. In Maid: **Phone** (toolbar) → tick *Let the paired phone connect* → **Pair a phone…**, and scan the code
   with the app (*Scan pairing code*). *Copy the pairing link instead* is there for a phone that cannot scan.

Security: Maid listens only on its Tailscale address (`remote.port`, default 47800; Windows Firewall lets in
only Tailscale addresses, for Maid's program). A connection must come from the device Tailscale says is the
paired phone, speak TLS with Maid's own certificate (the app pinned its fingerprint from the QR code), and
sign a fresh challenge with the key the app made in the phone's keystore at pairing (it cannot be copied
off the phone). Pairing codes work once, for five minutes, while the dialog shows them. One phone is paired
at a time; *Forget this phone* drops it at once. Every connection is logged (dialog, and `remote.log` in
the data folder). Remote access is off until you switch it on.

The phone only does what the page's buttons do (start, resume, stop, close windows; type into them); it
cannot change the list or settings. A console has one size: the window the phone shows gets the phone's size,
and gives it back when the phone looks elsewhere, goes to the background, or you type at the PC (the PC tile
says *Sized for the phone* meanwhile; a click there takes it back).

Build: `cd android; .\gradlew.bat assembleRelease` → `android\dist\Maid.apk` (needs the Android SDK and
JDK 17; it signs with your key in `android\keystore\keystore.properties` (`storeFile`, `storePassword`, `keyAlias`,
`keyPassword`), or with the debug key when there is none. Keep that key: an update installs over the app only when it
is signed with the same one).

## Controlling Maid from Claude (the API)

Every session Maid starts has `maidctl` on its PATH, so Claude Code can drive Maid itself: `maidctl windows`
(what runs and what each session is doing), `maidctl ask <project> "<task>"` (gives a project's session a task and
prints its answer), `maidctl launch`, `send`, `wait`, `screen`, `keys`, `stop`, … (`maidctl help`). It talks to
Maid through a local named pipe that only the user's own (elevated) programs can open. A voice assistant app on the phone
reaches the same API through the phone app. Reference: [docs/API.md](docs/API.md).

## Configuration

State lives in `%APPDATA%\Maid\state.json` (entries, groups and their colours, the shared info, font size,
layout, window placement). Edit its
`settings` (while Maid is closed): `command` is what gets launched, e.g. add `--model …`; `hubPath` the folder of the *All projects* session; `runAsAdmin`, `autoTrust`, `saveHistory`
and `historyMaxChars` are described above; `elevenLabsApiKey` (empty: the `ELEVENLABS_API_KEY` environment
variable), `transcriptionModel` (`scribe_v2`) and `dictationLanguage` (e.g. `cs`; empty: detected each time)
are for the phone's dictation. `voicePipe` (`Maid.Voice`; empty: none) is the local pipe a push-to-talk program talks to,
`voiceArguments` (`--model opus --effort xhigh`; a state.json still holding the earlier default, `--model sonnet --effort medium`, is moved to it) what the voice window's Claude Code gets on top of `command`. `apiPipe` (`Maid.Api`; empty: none) is the pipe of Maid's API. `agents-md.json` next to it remembers what each project's
`CLAUDE.md` and `AGENTS.md` last agreed on.
Set `MAID_DATA_DIR` to keep state and the browser profile somewhere else.

## How it works

- `src/Maid` is a small WPF shell hosting a single WebView2; the whole UI is `wwwroot/` (plain HTML/CSS/JS).
- Each session is a process attached to a Windows pseudo console (ConPTY, `Terminal/PtySession.cs`). Its VT
  output is streamed to an [xterm.js](https://xtermjs.org) terminal in the page; keystrokes travel back.
  `Dashboard.cs` is the JSON message bridge between the two sides.
- xterm.js and its addons are vendored in `wwwroot/lib` (no network access needed). To update them:
  `npm pack`/install `@xterm/xterm @xterm/addon-fit @xterm/addon-webgl @xterm/addon-unicode11 @xterm/addon-web-links`
  and copy each package's `lib/*.js` (plus `css/xterm.css`) over.
- `ConversationHistory.cs` reads Claude Code's transcripts (`~/.claude/projects/<folder>/<session>.jsonl`) and
  Codex's (`~/.codex/sessions/…/rollout-*.jsonl`) and maintains the history section of `CLAUDE.md`;
  `SharedInfo.cs` maintains the shared info section; `ClaudeMdFile.cs` writes both and keeps `AGENTS.md` in
  step; `Hub.cs` is the *All projects* folder; `FolderMove.cs` moves a project. `dotnet test` covers them.
- `tools/cdp.js` drives the UI over the WebView2 remote-debugging port for end-to-end testing;
  `tools/make-icon.ps1` regenerates the app icon.

## License

MIT, see [LICENSE](LICENSE). Bundled third-party components keep their own licences (`installer\THIRD-PARTY-NOTICES.txt`; xterm.js in `src/Maid/wwwroot/lib`, Cascadia Mono in `android/web/fonts`).
