# Builds into .\dist:
#   Shoji-Setup.exe         – the installer: always opens the Install / Update dialog
#   Shoji.exe               – the same program; offers to install when run from outside its install folder
#   Shoji-portable.zip      – the same exe plus a "portable" marker file: unzip anywhere and run
#   default              : single self-contained exe (~80 MB, runs on any Windows 10/11 x64 PC)
#   -FrameworkDependent  : smaller exe (~30 MB) that needs the ".NET 8 Desktop Runtime" installed
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
param(
    [string]$Runtime = "win-x64",
    [string]$Output = "dist",
    [switch]$FrameworkDependent
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$extra = if ($FrameworkDependent) {
    @("--self-contained", "false")
} else {
    @("--self-contained", "true", "-p:EnableCompressionInSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
}

dotnet publish "$root\src\Shoji\Shoji.csproj" `
    -c Release -r $Runtime `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -o "$root\$Output" @extra
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Installer: the same exe under a setup name (a name containing "setup" always shows the install dialog).
Copy-Item "$root\$Output\Shoji.exe" "$root\$Output\Shoji-Setup.exe" -Force

# Portable zip: exe + marker file in a Shoji folder.
$stage = Join-Path $env:TEMP "Shoji-portable"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$stage\Shoji" | Out-Null
Copy-Item "$root\$Output\Shoji.exe" "$stage\Shoji\"
Set-Content "$stage\Shoji\portable" "This file makes Shoji portable: settings are kept in the 'data' folder next to the exe. Delete it to use %APPDATA%\Shoji instead."
Compress-Archive -Path "$stage\Shoji" -DestinationPath "$root\$Output\Shoji-portable.zip" -Force
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Done: $root\$Output\Shoji-Setup.exe (installer)" -ForegroundColor Green
Write-Host "      $root\$Output\Shoji.exe" -ForegroundColor Green
Write-Host "      $root\$Output\Shoji-portable.zip" -ForegroundColor Green
