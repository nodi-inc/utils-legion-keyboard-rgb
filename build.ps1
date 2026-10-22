# Builds dist\LegionKbdRgb-Setup-<version>.exe with the .NET Framework 4 compiler that ships with Windows.
#
# Code signing (optional): if signing.json exists next to this script, both the application and the
# installer are Authenticode-signed with Azure Artifact Signing (formerly Trusted Signing). See
# docs/code-signing.md. Pass -RequireSigning to fail instead of producing an unsigned build.
param([switch]$RequireSigning)

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

$signingConfig = Join-Path $root 'signing.json'
$signing = $null
if (Test-Path $signingConfig) {
  $signing = Get-Content $signingConfig -Raw | ConvertFrom-Json
  if (-not (Get-Module -ListAvailable ArtifactSigning)) {
    throw "signing.json found but the ArtifactSigning module is missing. Run: Install-Module ArtifactSigning -Scope CurrentUser"
  }
} elseif ($RequireSigning) {
  throw "signing.json not found; cannot produce a signed build."
} else {
  Write-Warning "signing.json not found: building UNSIGNED binaries (Smart App Control may block them)."
}

function Sign([string]$file) {
  if (-not $signing) { return }
  Write-Host "Signing $file"
  # Authenticate with Azure CLI (az login), Azure PowerShell or AZURE_* environment variables.
  Invoke-ArtifactSigning -Endpoint $signing.Endpoint -CodeSigningAccountName $signing.CodeSigningAccountName `
    -CertificateProfileName $signing.CertificateProfileName -Files $file -FileDigest SHA256 `
    -TimestampRfc3161 'http://timestamp.acs.microsoft.com' -TimestampDigest SHA256 `
    -Description 'LegionKbdRgb' -DescriptionUrl 'https://github.com/nodi-inc/utils-legion-keyboard-rgb' `
    -ExcludeManagedIdentityCredential -ExcludeWorkloadIdentityCredential -ExcludeSharedTokenCacheCredential `
    -ExcludeVisualStudioCredential -ExcludeVisualStudioCodeCredential -ExcludeInteractiveBrowserCredential
  $sig = Get-AuthenticodeSignature $file
  if ($sig.Status -ne 'Valid') { throw "Signature check failed for ${file}: $($sig.Status) $($sig.StatusMessage)" }
}

$version = ([regex]::Match((Get-Content "$src\Version.cs" -Raw), 'Display = "([^"]+)"')).Groups[1].Value
# Committed icon (regenerate with src\tools\make-icon.ps1). The build only runs the signed
# Windows compiler, so Smart App Control has no unsigned helper executable to block.
$ico = "$src\app.ico"

$app = "$build\LegionKbdRgb.exe"
Compile @('/target:winexe', "/out:$app", "/win32icon:$ico", "/win32manifest:$src\app.manifest",
  "/resource:$ico,app.ico", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  "$src\KbdRgb.cs", "$src\hidutil.cs", "$src\Version.cs")
Sign $app   # sign before embedding: the installer writes this exact file to disk

$setup = "$dist\LegionKbdRgb-Setup-$version.exe"
Compile @('/target:winexe', "/out:$setup", "/win32icon:$ico", "/win32manifest:$src\app.manifest",
  "/resource:$ico,app.ico", "/resource:$app,LegionKbdRgb.exe",
  '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
  "$src\setup\Setup.cs", "$src\hidutil.cs", "$src\Version.cs")
Sign $setup  # also installed as uninstall.exe

# Only the setup exe is a deliverable; drop intermediates so no stray LegionKbdRgb.exe is left around.
Remove-Item $build -Recurse -Force
Get-Item $setup | Select-Object FullName, Length, @{ n = 'Signature'; e = { (Get-AuthenticodeSignature $_.FullName).Status } }
