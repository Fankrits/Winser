<#
.SYNOPSIS
  Walks through the memory verification steps from the lightweight/low-memory plan
  (Phase 3 / "Verification" section) and does the measuring, timing, and arithmetic, so
  running it is the whole job instead of babysitting a stopwatch across four manual samples.

.DESCRIPTION
  This cannot open tabs or click anything in Winser itself - PowerShell has no way to drive
  a WinUI 3 window's UI, and scripting that reliably is a much bigger job than the four
  numbers this actually needs. What it automates is everything else: summing
  msedgewebview2's working set at each of the plan's four checkpoints, reading the real
  configured discard threshold out of settings.json instead of assuming the 30-minute
  default, sleeping for exactly that long so you don't have to watch a clock, and printing
  a summary in the same table shape README.md's Size section already uses.

.EXAMPLE
  .\tools\measure-memory.ps1
  Uses the discard threshold from %LOCALAPPDATA%\Winser\Data\settings.json (or 30 minutes
  if that file doesn't exist yet).

.EXAMPLE
  .\tools\measure-memory.ps1 -DiscardMinutesOverride 2
  Skips reading settings.json and waits only 2 minutes before the discard measurement -
  set winser://settings' idle threshold to match before running, so what you're waiting for
  and what the script waits for agree.
#>
param(
  [int]$DiscardMinutesOverride = 0
)

function Measure-WebViewMemory {
    $procs = Get-Process msedgewebview2 -ErrorAction SilentlyContinue
    if (-not $procs) {
        return $null
    }

    $bytes = ($procs | Measure-Object WorkingSet64 -Sum).Sum
    [pscustomobject]@{
        ProcessCount = $procs.Count
        MB           = [math]::Round($bytes / 1MB, 1)
    }
}

function Wait-ForWebView {
    param([string]$FailureMessage)

    $sample = Measure-WebViewMemory
    if (-not $sample) {
        Write-Error $FailureMessage
        exit 1
    }
    return $sample
}

function Read-DiscardThresholdMinutes {
    if ($DiscardMinutesOverride -gt 0) {
        return $DiscardMinutesOverride
    }

    $settingsPath = Join-Path $env:LOCALAPPDATA 'Winser\Data\settings.json'
    if (Test-Path $settingsPath) {
        try {
            $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
            if ($null -ne $settings.discardIdleTabsAfterMinutes) {
                return [int]$settings.discardIdleTabsAfterMinutes
            }
        } catch {
            Write-Warning "Could not parse $settingsPath ($($_.Exception.Message)) - falling back to the 30-minute default."
        }
    }

    return 30
}

Write-Host "=== Winser memory verification ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Before continuing: open Winser, if it isn't already, and open 10 tabs on real"
Write-Host "sites (not winser://newtab - an unloaded tab has nothing to measure). Leave the"
Write-Host "first tab selected."
Read-Host "Press Enter once that's done"

$baseline = Wait-ForWebView -FailureMessage `
    "No msedgewebview2 processes found. Is Winser running with at least one tab loaded?"
Write-Host ("Baseline (10 tabs open): {0:N1} MB across {1} processes" -f $baseline.MB, $baseline.ProcessCount) -ForegroundColor Green

Write-Host ""
Write-Host "Now switch to a *different* tab than the one you started on, so the other nine"
Write-Host "go to the background and freeze (CoreWebView2.TrySuspendAsync). Give it about"
Write-Host "15 seconds after switching."
Read-Host "Press Enter once you've switched and waited"

$afterFreeze = Wait-ForWebView -FailureMessage "msedgewebview2 disappeared entirely - is Winser still running?"
Write-Host ("After freeze (9 of 10 backgrounded): {0:N1} MB across {1} processes" -f $afterFreeze.MB, $afterFreeze.ProcessCount) -ForegroundColor Green

$discardMinutes = Read-DiscardThresholdMinutes
if ($discardMinutes -le 0) {
    Write-Host ""
    Write-Warning "The configured discard threshold is 0 (never discard). Set a real value in winser://settings, or pass -DiscardMinutesOverride, and run this again from the top."
    exit 1
}

Write-Host ""
Write-Host "Waiting $discardMinutes minute(s) for the idle-discard threshold to elapse -"
Write-Host "don't touch the background tabs in the meantime. This is unattended; leave it running."
$deadline = (Get-Date).AddMinutes($discardMinutes)
while ((Get-Date) -lt $deadline) {
    $remaining = [math]::Ceiling(($deadline - (Get-Date)).TotalMinutes)
    Write-Host "`r  ~$remaining minute(s) remaining...  " -NoNewline
    Start-Sleep -Seconds 15
}
Write-Host "`r  Wait complete.                        "

$afterDiscard = Wait-ForWebView -FailureMessage "msedgewebview2 disappeared entirely - is Winser still running?"
Write-Host ("After discard: {0:N1} MB across {1} processes" -f $afterDiscard.MB, $afterDiscard.ProcessCount) -ForegroundColor Green

Write-Host ""
Write-Host "Click back onto one of the background tabs now. Confirm by eye: it should reload"
Write-Host "(a discard is a fresh navigation, not a resume - see README.md's Memory section)"
Write-Host "and land back on the same URL."
$reloaded = Read-Host "Did it reload to the same page? (y/n)"

$afterReturn = Wait-ForWebView -FailureMessage "msedgewebview2 disappeared entirely - is Winser still running?"

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host ""
$report = @()
$report += [pscustomobject]@{ Checkpoint = "Baseline (10 tabs open)"; MB = $baseline.MB; Processes = $baseline.ProcessCount }
$report += [pscustomobject]@{ Checkpoint = "After freeze (9 backgrounded)"; MB = $afterFreeze.MB; Processes = $afterFreeze.ProcessCount }
$report += [pscustomobject]@{ Checkpoint = "After discard ($discardMinutes min idle)"; MB = $afterDiscard.MB; Processes = $afterDiscard.ProcessCount }
$report += [pscustomobject]@{ Checkpoint = "After returning to a discarded tab"; MB = $afterReturn.MB; Processes = $afterReturn.ProcessCount }
$report | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host "Discarded tab reloaded correctly: $reloaded"
Write-Host ""
Write-Host "Markdown for README.md's Memory section:" -ForegroundColor Cyan
Write-Host ""
Write-Host "| Checkpoint | MB | Processes |"
Write-Host "|---|---:|---:|"
foreach ($row in $report) {
    Write-Host ("| {0} | {1:N1} | {2} |" -f $row.Checkpoint, $row.MB, $row.Processes)
}
