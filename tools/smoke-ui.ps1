param(
    [Parameter(Mandatory)]
    [string] $ExePath,
    [switch] $KeepProfile
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$profileRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ("ui-smoke-{0}" -f [guid]::NewGuid().ToString('N'))))
$screenshotRoot = Join-Path $artifactsRoot 'ux-review'
$screenshotPath = Join-Path $screenshotRoot ("winser-ui-smoke-{0}.png" -f [guid]::NewGuid().ToString('N'))
$normalRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Winser'))
$serverRoot = Join-Path $profileRoot 'http'
$serverProcess = $null
$app = $null

if ($profileRoot.StartsWith($normalRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $normalRoot.StartsWith($profileRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a smoke profile that overlaps the normal profile: $profileRoot"
}
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $profileRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a smoke profile outside artifacts: $profileRoot"
}
if (Test-Path -LiteralPath $profileRoot) { throw "Refusing to reuse an existing smoke profile: $profileRoot" }

New-Item -ItemType Directory -Path $profileRoot, (Join-Path $profileRoot 'Data'), $serverRoot -Force | Out-Null
New-Item -ItemType Directory -Path $screenshotRoot -Force | Out-Null
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
    Add-Type -AssemblyName System.Drawing

    function Save-WindowScreenshot($element, $path) {
        $bounds = $element.Current.BoundingRectangle
        $left = [Math]::Max(0, [int][Math]::Floor($bounds.Left))
        $top = [Math]::Max(0, [int][Math]::Floor($bounds.Top))
        $width = [int][Math]::Ceiling($bounds.Width)
        $height = [int][Math]::Ceiling($bounds.Height)
        if ($width -le 0 -or $height -le 0) { return }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($left, $top, 0, 0, [Drawing.Size]::new($width, $height))
            $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    $listener.Stop()
    $python = (Get-Command python.exe -ErrorAction Stop | Where-Object { $_.Source -notlike '*WindowsApps*' } | Select-Object -First 1).Source
    if (-not $python) { throw 'A direct python.exe installation is required for the UI smoke server.' }
    $serverArgs = "-m http.server $port --bind 127.0.0.1 --directory `"$serverRoot`""
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
        $app = Start-Process -FilePath $exe -PassThru -WindowStyle Normal
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
    $main.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Resize(800, 420)
    Start-Sleep -Milliseconds 500

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

    function Wait-UiGone($name, $type = $null, $visibleOnly = $false) {
        for ($i = 0; $i -lt 40; $i++) {
            if ($null -eq (Find-Ui $name $type $visibleOnly)) { return }
            Start-Sleep -Milliseconds 250
        }
        throw "UI Automation element remained visible: $name"
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
    $bareAddress = "127.0.0.1:$port/"
    [Windows.Forms.SendKeys]::SendWait($bareAddress)
    $enteredValue = $address.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($enteredValue -ne $bareAddress) { throw "Address box did not contain the typed bare host: $enteredValue" }
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Wait-Ui 'Site information' ([Windows.Automation.ControlType]::Button) | Out-Null

    # The tab title confirms WebView2 loaded the loopback page, not just the address input.
    $showTabs = Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    Press-Enter (Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button))
    Wait-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true | Out-Null
    Wait-Ui 'Winser smoke page' ([Windows.Automation.ControlType]::ListItem) $true | Out-Null
    $siteInfo = Wait-Ui 'Site information' ([Windows.Automation.ControlType]::Button)
    $siteInfo.SetFocus()
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Wait-UiGone 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true

    Press-Enter $siteInfo
    Wait-Ui '127.0.0.1' $null $true | Out-Null
    Wait-Ui $url $null $true | Out-Null
    [Windows.Forms.SendKeys]::SendWait('{ESC}')

    $menu = Wait-Ui 'Settings and more' ([Windows.Automation.ControlType]::Button)
    Press-Enter $menu
    Wait-Ui 'Settings' $null $true | Out-Null
    [Windows.Forms.SendKeys]::SendWait('{ESC}')

    # Give the pane enough tabs to scroll, then verify keyboard selection stays visible.
    for ($i = 0; $i -lt 12; $i++) { [Windows.Forms.SendKeys]::SendWait('^t'); Start-Sleep -Milliseconds 150 }

    # Open the unpinned pane, verify focus keeps it open after pointer exit, then verify focus-out closes it.
    Press-Enter (Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button))
    Wait-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true | Out-Null
    $list = Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true
    $list.SetFocus()
    $scroll = $list.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    [Windows.Forms.SendKeys]::SendWait('{HOME}')
    Start-Sleep -Milliseconds 250
    $startScroll = $scroll.Current.VerticalScrollPercent
    $firstSelection = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).GetSelection()
    [Windows.Forms.SendKeys]::SendWait('{END}')
    Start-Sleep -Milliseconds 400
    $selected = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).GetSelection()
    if ($selected.Count -eq 0 -or $firstSelection.Count -eq 0 -or
        $selected[0].Current.Name -eq $firstSelection[0].Current.Name -or $selected[0].Current.IsOffscreen) {
        throw 'Keyboard selection did not scroll the selected tab into view.'
    }
    if ($scroll.Current.VerticalScrollPercent -le $startScroll) {
        throw 'Selecting the last tab did not scroll the overflowing tab list.'
    }
    $listBounds = $list.Current.BoundingRectangle
    $selectedBounds = $selected[0].Current.BoundingRectangle
    if ($selectedBounds.Top -lt $listBounds.Top -or $selectedBounds.Bottom -gt $listBounds.Bottom) {
        throw 'The keyboard-selected tab is not inside the visible list viewport.'
    }
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Start-Sleep -Milliseconds 800
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'The vertical tab pane closed while its list retained keyboard focus.'
    }
    [Windows.Forms.SendKeys]::SendWait('{DOWN}')
    [Windows.Forms.SendKeys]::SendWait('{UP}')
    Start-Sleep -Milliseconds 300
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'Keyboard arrow navigation closed the focused vertical tab pane.'
    }
    $leftByTab = $false
    for ($i = 0; $i -lt 6; $i++) {
        [Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 250
        if ($null -ne (Find-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)) -and
            $null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
            $leftByTab = $true
            break
        }
    }
    if (-not $leftByTab) {
        throw 'Tab focus leaving the vertical tab list did not close the unpinned pane.'
    }

    # Pinning must keep the pane open after the pointer exits.
    $showTabs = Find-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    Press-Enter $showTabs
    $pin = Wait-Ui 'Pin the tab pane open' ([Windows.Automation.ControlType]::Button)
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
    if ($null -ne $main) {
        try {
            Save-WindowScreenshot $main $screenshotPath
            Write-Output "UI screenshot: $screenshotPath"
        }
        catch { Write-Warning "Could not save UI screenshot: $_" }
    }
    if ($null -ne $app -and -not $app.HasExited) {
        [void] $app.CloseMainWindow()
        if (-not $app.WaitForExit(10000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not $KeepProfile) {
        $deletePath = [IO.Path]::GetFullPath($profileRoot)
        if ($deletePath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            -not ($deletePath.StartsWith($normalRoot, [StringComparison]::OrdinalIgnoreCase) -or
                  $normalRoot.StartsWith($deletePath, [StringComparison]::OrdinalIgnoreCase))) {
            $deleteItem = Get-Item -LiteralPath $deletePath -ErrorAction SilentlyContinue
            if ($null -ne $deleteItem -and -not ($deleteItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                Remove-Item -LiteralPath $deletePath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        else { Write-Warning "Retained profile because its resolved path failed the cleanup guard: $deletePath" }
    }
}
