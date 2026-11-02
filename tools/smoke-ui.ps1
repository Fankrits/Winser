param(
    [Parameter(Mandatory)]
    [string] $ExePath,
    [switch] $KeepProfile
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$profileRoot = Join-Path $repo (Join-Path 'artifacts' ("ui-smoke-{0}" -f [guid]::NewGuid().ToString('N')))
$normalRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Winser'))
$serverRoot = Join-Path $profileRoot 'http'
$serverProcess = $null
$app = $null

if ($profileRoot.StartsWith($normalRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $normalRoot.StartsWith($profileRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a smoke profile that overlaps the normal profile: $profileRoot"
}

New-Item -ItemType Directory -Path $profileRoot, (Join-Path $profileRoot 'Data'), $serverRoot -Force | Out-Null
@{
    theme = 0
    startup = 0
    homePage = 'winser://newtab'
    useVerticalTabs = $true
    verticalTabsPinned = $false
    clearHistoryOnExit = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profileRoot 'Data\settings.json') -Encoding utf8
@'
<!doctype html><meta charset="utf-8"><title>Winser smoke page</title>
<main><h1>Winser smoke page loaded</h1><p>Loopback navigation succeeded.</p></main>
'@ | Set-Content -LiteralPath (Join-Path $serverRoot 'index.html') -Encoding utf8

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    $listener.Stop()
    $python = (Get-Command py -ErrorAction Stop).Source
    $serverArgs = "-3 -m http.server $port --bind 127.0.0.1 --directory `"$serverRoot`""
    $serverProcess = Start-Process -FilePath $python -ArgumentList $serverArgs -PassThru -WindowStyle Hidden
    $url = "http://127.0.0.1:$port/"
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 1 | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "Loopback server did not become ready at $url" }

    $prior = $env:WINSER_USER_DATA_FOLDER
    try {
        $env:WINSER_USER_DATA_FOLDER = $profileRoot
        $app = Start-Process -FilePath $exe -PassThru
    }
    finally {
        if ($null -eq $prior) { Remove-Item Env:WINSER_USER_DATA_FOLDER -ErrorAction SilentlyContinue }
        else { $env:WINSER_USER_DATA_FOLDER = $prior }
    }

    $desktop = [Windows.Automation.AutomationElement]::RootElement
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $main = $null
    for ($i = 0; $i -lt 80 -and $null -eq $main; $i++) {
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $main = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        }
        else { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $main) { throw 'Winser did not create a main window.' }

    function Find-Ui($name, $type = $null, $visibleOnly = $false) {
        $nameCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, $name)
        $condition = $nameCondition
        if ($null -ne $type) {
            $typeCondition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
            $condition = [Windows.Automation.AndCondition]::new($nameCondition, $typeCondition)
        }
        foreach ($window in $desktop.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)) {
            $element = $window.FindFirst([Windows.Automation.TreeScope]::Subtree, $condition)
            if ($null -ne $element -and (-not $visibleOnly -or -not $element.Current.IsOffscreen)) { return $element }
        }
        return $null
    }

    function Wait-Ui($name, $type = $null, $visibleOnly = $false) {
        for ($i = 0; $i -lt 40; $i++) {
            $element = Find-Ui $name $type $visibleOnly
            if ($null -ne $element) { return $element }
            Start-Sleep -Milliseconds 250
        }
        throw "UI Automation element not found: $name"
    }

    function Press-Enter($element) {
        $element.SetFocus()
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }

    # The app's WebView profile directory is the in-process proof that it resolved our override.
    for ($i = 0; $i -lt 40 -and -not (Test-Path (Join-Path $profileRoot 'Profile')); $i++) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path (Join-Path $profileRoot 'Profile'))) {
        throw "Winser did not create its WebView profile under the isolated root: $profileRoot"
    }
    $showTabs = Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    $address = Wait-Ui 'Search or enter web address'

    # Exercise navigation and the site info/menu controls using only the disposable profile.
    $address.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('^a')
    [Windows.Forms.SendKeys]::SendWait($url)
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Wait-Ui 'Site information' ([Windows.Automation.ControlType]::Button) | Out-Null
    $siteInfo = Find-Ui 'Site information' ([Windows.Automation.ControlType]::Button)
    Press-Enter $siteInfo
    Wait-Ui "127.0.0.1:$port" $null $true | Out-Null
    [Windows.Forms.SendKeys]::SendWait('{ESC}')

    $menu = Wait-Ui 'Settings and more' ([Windows.Automation.ControlType]::Button)
    Press-Enter $menu
    Wait-Ui 'Settings' $null $true | Out-Null
    [Windows.Forms.SendKeys]::SendWait('{ESC}')

    # Give the pane enough tabs to scroll, then verify keyboard selection stays visible.
    for ($i = 0; $i -lt 7; $i++) { [Windows.Forms.SendKeys]::SendWait('^t'); Start-Sleep -Milliseconds 150 }

    # Open the unpinned pane, verify focus keeps it open after pointer exit, then verify focus-out closes it.
    Press-Enter $showTabs
    Wait-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true | Out-Null
    $list = Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true
    $listItems = $list.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem))
    if ($listItems.Count -lt 8) { throw "Expected at least 8 tabs in the smoke list, found $($listItems.Count)." }
    $list.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{END}')
    Start-Sleep -Milliseconds 400
    $selected = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).GetSelection()
    if ($selected.Count -eq 0 -or $selected[0].Current.IsOffscreen) {
        throw 'Keyboard selection did not scroll the selected tab into view.'
    }
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Start-Sleep -Milliseconds 800
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'The vertical tab pane closed while its list retained keyboard focus.'
    }
    $menu.SetFocus()
    Start-Sleep -Milliseconds 800
    if ($null -eq (Find-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button))) {
        throw 'The pane action did not return to Show tabs after focus and pointer left.'
    }

    # Pinning must keep the pane open after the pointer exits.
    $showTabs = Find-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    Press-Enter $showTabs
    $pin = Wait-Ui 'Pin vertical tabs' ([Windows.Automation.ControlType]::Button)
    Press-Enter $pin
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Start-Sleep -Milliseconds 800
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'Pinning did not keep the vertical tab pane open after pointer exit.'
    }

    Write-Output "UI smoke passed: isolated profile $profileRoot; loopback page $url"
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        [void] $app.CloseMainWindow()
        if (-not $app.WaitForExit(10000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not $KeepProfile) { Remove-Item -LiteralPath $profileRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
