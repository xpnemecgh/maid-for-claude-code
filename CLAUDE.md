# Maid

A Windows dashboard for Claude Code sessions: a WPF window hosting one WebView2 page; every listed project folder
can run Claude Code (`claude --dangerously-skip-permissions`) in a ConPTY terminal shown as an xterm.js tile.
User-facing behaviour is described in README.md, Maid's API in docs/API.md.

## Build, test, run

- `dotnet build Maid.sln` · `dotnet test Maid.sln` · `dotnet run --project src\Maid`
- `dotnet publish src\Maid\Maid.csproj -c Release -o dist\Maid` (framework-dependent); `tools\build-installer.ps1`
  builds the self-contained installer (Inno Setup 6, `installer\Maid.iss`).
- A running Maid.exe locks its build output. Changes to `src\Maid\wwwroot` need no rebuild: copy them into the build
  output's `wwwroot` and reload the page.
- Raise `<Version>` in `src\Maid\Maid.csproj` and `src\MaidCtl\MaidCtl.csproj` with each release (shown in the
  window title).

## Layout

- `src/Maid`: `MainWindow` (the WPF shell), `Dashboard*.cs` (the JSON message bridge to the page; sessions, the list,
  hooks, the API, the voice pipe), `Terminal/` (ConPTY sessions, launch command, Claude Code's HTTP hooks),
  `Remote/` (the local named pipes of the API and the voice window), `ConversationHistory.cs`, `SharedInfo.cs`,
  `ClaudeMdFile.cs` (CLAUDE.md/AGENTS.md sections and syncing), `Hub.cs` (All projects), `FolderMove.cs`,
  `Elevation.cs` (Maid restarts itself elevated by default).
- `src/Maid/wwwroot`: the whole UI (`index.html`, `app.css`, `app.js`, `termcolor.js`; xterm.js vendored in `lib`).
- `src/MaidCtl`: `maidctl`, the API's command line, copied into `cli\` next to Maid.exe.
- `tests/Maid.Tests`: xUnit tests that drive `Dashboard.HandleMessage` as the page would.
- `tools/cdp.js`: drives the real UI through the WebView2 remote-debugging port (usage in its header; start Maid with
  a scratch `MAID_DATA_DIR` whose `state.json` has `"runAsAdmin": false`, since an elevated WebView2 opens no port).

## Conventions

- Maid opens no network port to the outside: its local pipes accept only the user's own programs (only elevated ones
  while Maid is elevated), and the hook server listens on 127.0.0.1 only. Keep it that way.
- Tests that start real `claude` sessions use scratch folders only and give them no prompts.
