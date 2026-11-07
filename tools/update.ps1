<#
.SYNOPSIS
Updates the Maid that runs from dist\Maid without stopping it now.

.DESCRIPTION
Publishes the current source to dist\Maid-next (unless -NoPublish), then waits until Maid is closed
(closing it saves every session's conversation, as always), puts the new build in place of dist\Maid
and starts Maid again. Sessions are not restarted: launch or resume them from the list.

Run it from outside Maid, or let it wait in the background as a one-off scheduled task, which keeps
waiting when the Maid it was started from closes:

    powershell -ExecutionPolicy Bypass -File tools\update.ps1 -Background
#>
param(
    [switch]$NoPublish,
    [switch]$Background,
    [int]$WaitHours = 24,
    # For trying the script out on a copy: another folder, and a process name that isn't running.
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$ProcessName = 'Maid'
)
$ErrorActionPreference = 'Stop'
$root = $Root
$dist = Join-Path $root 'dist\Maid'
$next = Join-Path $root 'dist\Maid-next'
$log = Join-Path $root 'dist\update.log'
$task = 'MaidUpdate'

function Note($text) { "$(Get-Date -Format 's') $text" | Tee-Object -FilePath $log -Append }

if (-not $NoPublish) {
    Note "publishing to $next"
    dotnet publish (Join-Path $root 'src\Maid\Maid.csproj') -c Release -o $next --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $next 'Maid.exe'))) { throw "No new build in $next" }

if ($Background) {
    # A scheduled task outlives this window and the Maid session it may have been started from.
    # A subst drive may not be visible to the task: it gets the real path.
    $script = $PSCommandPath
    $mapping = subst | Where-Object { $_ -like "$(Split-Path -Qualifier $script)\*" } | Select-Object -First 1
    if ($mapping) { $script = ($mapping -split ' => ', 2)[1].TrimEnd('\') + $script.Substring(2) }
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -NoPublish -WaitHours $WaitHours"
    schtasks /create /tn $task /tr $command /sc once /st 23:59 /rl HIGHEST /f | Out-Null
    schtasks /run /tn $task | Out-Null
    Note "waiting in the background (task $task) for Maid to close"
    return
}

$deadline = (Get-Date).AddHours($WaitHours)
while (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { Note 'Maid stayed open; not updated'; exit 1 }
    Start-Sleep -Seconds 2
}
# Its sessions may still be saving their conversations for a moment after the window is gone.
Start-Sleep -Seconds 3

Note "updating $dist"
robocopy $next $dist /MIR /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Note "copy failed (robocopy $LASTEXITCODE); dist\Maid-next is kept"; exit 1 }
Remove-Item $next -Recurse -Force
Note 'starting Maid'
Start-Process (Join-Path $dist 'Maid.exe')
# The one-off task is done (when it ran as one); there is nothing to remove otherwise.
$ErrorActionPreference = 'Continue'
schtasks /delete /tn $task /f *> $null
