<#
.SYNOPSIS
    Publishes Aver Launcher and installs it for the current user.

.DESCRIPTION
    Produces a self-contained single-file AverLauncher.exe, copies it to
    %LOCALAPPDATA%\Programs\Aver Launcher, and creates a Start menu shortcut so the app can be
    found the way every other app is found.

    WHY SELF-CONTAINED. The launcher's job includes telling a user their machine has no .NET 10
    runtime. An app that needs .NET 10 in order to deliver that message cannot deliver it. A
    framework-dependent build is about 2 MB against roughly 150 MB here, and that trade is worth it
    exactly once -- for the one executable whose whole purpose is bootstrapping.

    WHY PER-USER. %LOCALAPPDATA%\Programs is the convention for installs that need no elevation, and
    it matches where engines already go. Nothing here touches HKLM or Program Files, so no UAC prompt
    and no administrator.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER Desktop
    Also drop a shortcut on the Desktop.

.PARAMETER StagingOnly
    Publish but do not install or create shortcuts. For producing a release artifact.

.EXAMPLE
    ./tools/publish-launcher.ps1
    ./tools/publish-launcher.ps1 -Desktop
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $Desktop,
    [switch] $StagingOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\Aver.Launcher.App\Aver.Launcher.App.csproj'
$out = Join-Path $repo "artifacts\launcher-$($Configuration.ToLower())"

Write-Host "[publish] $Configuration -> $out"

# A running copy holds its own exe open, so publishing over it fails with a lock error that reads
# like a build problem.
$running = Get-Process AverLauncher -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[publish] Aver Launcher is running (pid $($running.Id -join ', ')); close it first" -ForegroundColor Yellow
    exit 1
}

# NOTE on trimming: deliberately NOT enabled. WPF resolves a great deal by reflection and through
# XAML-generated code, and a trimmed build fails at run time when a control template asks for a type
# the trimmer decided nothing referenced. Size is not worth a crash that only appears in the shipped
# configuration.
& dotnet publish $proj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { Write-Host '[publish] dotnet publish failed' -ForegroundColor Red; exit 1 }

$exe = Join-Path $out 'AverLauncher.exe'
if (-not (Test-Path -LiteralPath $exe)) { Write-Host "[publish] no AverLauncher.exe in $out" -ForegroundColor Red; exit 1 }

$sizeMb = (Get-Item $exe).Length / 1MB
Write-Host ("[publish] AverLauncher.exe {0:N1} MB" -f $sizeMb)

# Prove it is a real Windows GUI executable rather than a console app or a script wrapper.
$bytes = [System.IO.File]::ReadAllBytes($exe)
$peOff = [BitConverter]::ToInt32($bytes, 0x3C)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOff + 0x5C)   # optional header + 0x44
$machine = [BitConverter]::ToUInt16($bytes, $peOff + 4)
$subsystemName = switch ($subsystem) { 2 { 'WINDOWS_GUI' } 3 { 'WINDOWS_CUI' } default { "unknown($subsystem)" } }
$machineName = switch ($machine) { 0x8664 { 'x64' } 0x14c { 'x86' } 0xAA64 { 'ARM64' } default { "0x{0:X}" -f $machine } }
Write-Host "[publish] PE subsystem $subsystemName, machine $machineName"
if ($subsystem -ne 2) { Write-Host '[publish] WARNING: not a GUI subsystem binary' -ForegroundColor Yellow }

if ($StagingOnly) { Write-Host "[publish] staging only; not installed"; exit 0 }

# ---- install for this user ----
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Aver Launcher'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $exe (Join-Path $installDir 'AverLauncher.exe') -Force
Write-Host "[publish] installed -> $installDir"

# ---- Start menu shortcut ----
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu 'Aver Launcher.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = Join-Path $installDir 'AverLauncher.exe'
$s.WorkingDirectory = $installDir
$s.IconLocation = (Join-Path $installDir 'AverLauncher.exe') + ',0'
$s.Description = 'Install and manage Aver Engine versions'
$s.Save()
Write-Host "[publish] Start menu -> $lnk"

if ($Desktop) {
    $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aver Launcher.lnk'
    $d = $shell.CreateShortcut($desk)
    $d.TargetPath = Join-Path $installDir 'AverLauncher.exe'
    $d.WorkingDirectory = $installDir
    $d.IconLocation = (Join-Path $installDir 'AverLauncher.exe') + ',0'
    $d.Save()
    Write-Host "[publish] Desktop -> $desk"
}

Write-Host ''
Write-Host '[publish] Done. Search the Start menu for "Aver Launcher".' -ForegroundColor Green
exit 0
