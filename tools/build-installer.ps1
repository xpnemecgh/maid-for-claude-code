<#
  Builds Maid's Windows installer, dist\Maid-Setup-<version>.exe, for other people to try Maid (the PC app only).

  A self-contained Maid (no .NET needed on the PC it goes to; maidctl one self-contained exe in cli\) is published into a
  staging folder outside the repository, the licences of what it bundles go next to it, and Inno Setup 6 packs it
  (installer\Maid.iss). The version is <Version> in src\Maid\Maid.csproj.

  Needs the .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 [-OutputDir <folder>] [-KeepStage]
#>
param(
    [string]$OutputDir,
    [switch]$KeepStage
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

[xml]$project = Get-Content (Join-Path $repo 'src\Maid\Maid.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'No <Version> in src\Maid\Maid.csproj.' }
$webView2 = @($project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Microsoft.Web.WebView2' })[0].Version

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw 'Inno Setup 6 was not found: winget install JRSoftware.InnoSetup' }

# Staged in the temp folder, not in the working tree: about 150 MB of runtime files.
$stage = Join-Path ([IO.Path]::GetTempPath()) 'Maid-installer'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$app = Join-Path $stage 'app'

Write-Host "Publishing Maid $version (self-contained, win-x64)..."
dotnet publish (Join-Path $repo 'src\Maid\Maid.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -o $app
if ($LASTEXITCODE) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

# The WebView2 package's API documentation is of no use to whoever runs Maid.
Get-ChildItem -LiteralPath $app -Filter 'Microsoft.Web.WebView2.*.xml' | Remove-Item

# cli\ is put on every session's PATH: it must hold maidctl alone, and maidctl must not need an installed .NET.
$cli = @(Get-ChildItem -LiteralPath (Join-Path $app 'cli'))
if ($cli.Count -ne 1 -or $cli[0].Name -ne 'maidctl.exe') { throw "cli\ should hold maidctl.exe alone, not: $($cli.Name -join ', ')" }
$help = & $cli[0].FullName help
if ($LASTEXITCODE -or -not ($help -match 'maidctl')) { throw 'The published maidctl.exe does not run.' }

# The licences of what Maid bundles.
$packages = ((dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', '').Trim()
$runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'Maid.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @{}
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) { $frameworks[$framework.name] = $framework.version }
$licenses = New-Item -ItemType Directory (Join-Path $app 'licenses')
function Copy-License([string]$From, [string]$Name) {
    if (-not (Test-Path -LiteralPath $From)) { throw "Licence file not found: $From" }
    Copy-Item -LiteralPath $From (Join-Path $licenses $Name)
}
Copy-License (Join-Path $repo 'installer\THIRD-PARTY-NOTICES.txt') 'THIRD-PARTY-NOTICES.txt'
Copy-License (Join-Path $packages "microsoft.netcore.app.runtime.win-x64\$($frameworks['Microsoft.NETCore.App'])\LICENSE.TXT") 'dotnet-LICENSE.txt'
Copy-License (Join-Path $packages "microsoft.netcore.app.runtime.win-x64\$($frameworks['Microsoft.NETCore.App'])\THIRD-PARTY-NOTICES.TXT") 'dotnet-THIRD-PARTY-NOTICES.txt'
Copy-License (Join-Path $packages "microsoft.windowsdesktop.app.runtime.win-x64\$($frameworks['Microsoft.WindowsDesktop.App'])\LICENSE") 'windowsdesktop-LICENSE.txt'
Copy-License (Join-Path $packages "microsoft.web.webview2\$webView2\LICENSE.txt") 'WebView2-LICENSE.txt'
Copy-License (Join-Path $packages "microsoft.web.webview2\$webView2\NOTICE.txt") 'WebView2-NOTICE.txt'
Copy-License (Join-Path $repo 'src\Maid\wwwroot\lib\XTERM-LICENSE.txt') 'xterm-LICENSE.txt'

Write-Host "Packing with Inno Setup ($iscc)..."
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$OutputDir" (Join-Path $repo 'installer\Maid.iss')
if ($LASTEXITCODE) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

if (-not $KeepStage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$setup = Get-Item -LiteralPath (Join-Path $OutputDir "Maid-Setup-$version.exe")
Write-Host ("{0}  {1:N1} MB  SHA-256 {2}" -f $setup.FullName, ($setup.Length / 1MB), (Get-FileHash -LiteralPath $setup.FullName).Hash)
