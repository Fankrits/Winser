#Requires -RunAsAdministrator
<#
  Run once per device before the first install of a Winser release: trusts the certificate
  that signed the package sitting next to this script, then installs it. Windows treats a
  self-signed package's certificate as untrusted until it is added to LocalMachine\TrustedPeople
  on that specific machine (0x800B010A, "publisher certificate could not be verified", is exactly
  that check failing) - this script is that one-time step, done for you instead of by hand.

  #Requires -RunAsAdministrator above means PowerShell refuses to run this at all, with its
  own clear message, if it wasn't launched elevated - Import-Certificate against LocalMachine
  needs that, and finding out after downloading and half-running the script is worse.

  Prefers installing through the .appinstaller manifest over the bare .msix when one is present:
  Add-AppxPackage -AppInstallerFile does the same install, but also registers Winser with
  Windows' App Installer so future releases install themselves automatically (see the "Generate
  the App Installer manifest" step in .github/workflows/release.yml) - this one run is then the
  only time this script, or any manual install step, should be needed. Falls back to the plain
  .msix if no .appinstaller is next to this script (e.g. an older release, or a manually
  assembled folder), which still installs Winser, just without enrolling it for auto-update.

  Finds its files by extension rather than a hardcoded filename, since all three carry a version
  number that changes every release and this script does not need to.
#>

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$cert = Get-ChildItem -Path $here -Filter *.cer | Select-Object -First 1
$appInstaller = Get-ChildItem -Path $here -Filter *.appinstaller | Select-Object -First 1
$msix = Get-ChildItem -Path $here -Filter *.msix | Select-Object -First 1

if (-not $cert) {
    Write-Error "No .cer file found next to this script in '$here' - download it from the release, alongside the .msix."
    exit 1
}

if (-not $appInstaller -and -not $msix) {
    Write-Error "No .appinstaller or .msix file found next to this script in '$here' - download the release files first."
    exit 1
}

Write-Host "Trusting $($cert.Name) (Cert:\LocalMachine\TrustedPeople)..."
Import-Certificate -FilePath $cert.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

if ($appInstaller) {
    Write-Host "Installing $($appInstaller.Name)..."
    Add-AppxPackage -AppInstallerFile $appInstaller.FullName
    Write-Host "Done - Winser is installed and will update itself automatically."
} else {
    Write-Host "Installing $($msix.Name)..."
    Add-AppxPackage -Path $msix.FullName
    Write-Host "Done - Winser is installed. No .appinstaller was found, so it will not update itself automatically; re-run this script against a future release, or grab that release's .appinstaller, to update."
}
