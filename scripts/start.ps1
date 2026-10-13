param([switch]$FromHook)
$ErrorActionPreference = 'Stop'
if ($FromHook) {
    $usageFollowCommand = [string](Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CodexUsageBar -ErrorAction SilentlyContinue).CodexUsageBar
    if (-not $usageFollowCommand.EndsWith(' --watch-codex', [StringComparison]::OrdinalIgnoreCase)) { return }
}
$pluginRoot = Split-Path -Parent $PSScriptRoot
$usageExe = Join-Path $pluginRoot 'bin\CodexUsageBar.exe'
Start-Process -FilePath $usageExe -WorkingDirectory $pluginRoot -WindowStyle Hidden
