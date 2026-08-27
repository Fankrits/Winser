#Requires -RunAsAdministrator
<#
  Run once per device before the first install of a Winser release: trusts the certificate
  that signed the .msix sitting next to this script, then installs the package directly.
  Windows treats a self-signed package's certificate as untrusted until it is added to
  LocalMachine\TrustedPeople on that specific machine (0x800B010A, "publisher certificate
  could not be verified", is exactly that check failing) - this script is that one-time step,
  done for you instead of by hand.

  #Requires -RunAsAdministrator above means PowerShell refuses to run this at all, with its
  own clear message, if it wasn't launched elevated - Import-Certificate against LocalMachine
  needs that, and finding out after downloading and half-running the script is worse.

  Finds its .cer and .msix by extension rather than a hardcoded filename, since both carry a
  version number that changes every release and this script does not need to.
#>

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$cert = Get-ChildItem -Path $here -Filter *.cer | Select-Object -First 1
$msix = Get-ChildItem -Path $here -Filter *.msix | Select-Object -First 1

if (-not $cert) {
    Write-Error "No .cer file found next to this script in '$here' - download it from the release, alongside the .msix."
    exit 1
}

if (-not $msix) {
    Write-Error "No .msix file found next to this script in '$here' - download it from the release, alongside the .cer."
    exit 1
}

Write-Host "Trusting $($cert.Name) (Cert:\LocalMachine\TrustedPeople)..."
Import-Certificate -FilePath $cert.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

Write-Host "Installing $($msix.Name)..."
Add-AppxPackage -Path $msix.FullName

Write-Host "Done - Winser is installed."
