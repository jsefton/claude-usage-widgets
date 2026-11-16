<#
.SYNOPSIS
  Builds Claude Usage for Windows as a single ClaudeUsage.exe in windows/build.

.PARAMETER SelfContained
  Bundle the .NET runtime into the exe (about 70 MB) so it runs on PCs without the
  .NET 10 Desktop Runtime. By default the exe is small and needs that runtime.

.PARAMETER Install
  Also copy the exe to %LOCALAPPDATA%\Programs\Claude Usage, add a Start menu shortcut, and launch it.

.EXAMPLE
  scripts\build.cmd -Install

  (build.cmd runs this script with the execution policy bypassed, since Windows blocks .ps1 files by default.)
#>
param(
    [switch]$SelfContained,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ClaudeUsageTray\ClaudeUsageTray.csproj'
$output = Join-Path $root 'build'

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $output,
    '-p:PublishSingleFile=true',
    '-p:DebugType=none'
)
if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
} else {
    $publishArgs += @('--self-contained', 'false')
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $output 'ClaudeUsage.exe'
Write-Host "Built $exe"

if ($Install) {
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\Claude Usage'
    $installed = Join-Path $installDir 'ClaudeUsage.exe'

    # Stop a running copy from this location so the exe can be replaced.
    Get-Process ClaudeUsage -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $installed } |
        ForEach-Object { $_.CloseMainWindow() | Out-Null; $_ | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Force $installDir | Out-Null
    Copy-Item $exe $installed -Force

    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Usage.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installed
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = "Today's Claude usage cost and budget"
    $shortcut.Save()

    Write-Host "Installed to $installed (Start menu: Claude Usage)"
    Start-Process $installed
}
