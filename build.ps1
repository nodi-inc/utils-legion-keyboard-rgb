# Builds dist\LegionKbdRgb-Setup-<version>.exe with the .NET Framework 4 compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $root 'src'
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $build, $dist | Out-Null

function Compile([string[]]$cscArgs) {
  & $csc /nologo /codepage:65001 /optimize @cscArgs
  if ($LASTEXITCODE -ne 0) { throw "csc failed: $cscArgs" }
}

$version = ([regex]::Match((Get-Content "$src\Version.cs" -Raw), 'Display = "([^"]+)"')).Groups[1].Value
$ico = "$build\app.ico"

Compile @("/out:$build\MakeIcon.exe", '/r:System.Drawing.dll', "$src\tools\MakeIcon.cs")
& "$build\MakeIcon.exe" $ico

Compile @('/target:winexe', "/out:$build\LegionKbdRgb.exe", "/win32icon:$ico", "/win32manifest:$src\app.manifest",
  "/resource:$ico,app.ico", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  "$src\KbdRgb.cs", "$src\hidutil.cs", "$src\Version.cs")

$setup = "$dist\LegionKbdRgb-Setup-$version.exe"
Compile @('/target:winexe', "/out:$setup", "/win32icon:$ico", "/win32manifest:$src\app.manifest",
  "/resource:$ico,app.ico", "/resource:$build\LegionKbdRgb.exe,LegionKbdRgb.exe",
  '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  "$src\setup\Setup.cs", "$src\hidutil.cs", "$src\Version.cs")

# Only the setup exe is a deliverable; drop intermediates so no stray LegionKbdRgb.exe is left around.
Remove-Item $build -Recurse -Force
Get-Item $setup | Select-Object FullName, Length
