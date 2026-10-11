# Builds dist\ClaudeStartupManager.exe (framework-dependent, single file).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path .\app.ico)) {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
}

if (Get-Process ClaudeStartupManager -ErrorAction SilentlyContinue) {
    throw 'ClaudeStartupManager.exe is running. Close it first so the exe can be replaced.'
}

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "Built $PSScriptRoot\dist\ClaudeStartupManager.exe"
