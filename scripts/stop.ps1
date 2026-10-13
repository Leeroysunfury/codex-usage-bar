$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $pluginRoot 'bin\CodexUsageBar.exe') --stop
