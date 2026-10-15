# Regenerates src\app.ico from MakeIcon.cs. Runs in-process via Add-Type so no unsigned
# helper executable is launched (Smart App Control may block those).
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'MakeIcon.cs') -ReferencedAssemblies System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'app.ico'
[MakeIcon]::Main(@($out))
Get-Item $out | Select-Object FullName, Length
