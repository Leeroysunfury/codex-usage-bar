$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$usageExe = Join-Path $pluginRoot 'bin\CodexUsageBar.exe'
$usageSource = Join-Path $pluginRoot 'src\UsageBar.cs'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$usageExe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll $usageSource
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
