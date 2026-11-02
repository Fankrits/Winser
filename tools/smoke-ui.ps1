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
$wideScreenshotPath = Join-Path $screenshotRoot ("winser-ui-smoke-800px-{0}.png" -f [guid]::NewGuid().ToString('N'))
$narrowScreenshotPath = Join-Path $screenshotRoot ("winser-ui-smoke-narrow-{0}.png" -f [guid]::NewGuid().ToString('N'))
$settingsScreenshotPath = Join-Path $screenshotRoot ("winser-ui-smoke-settings-{0}.png" -f [guid]::NewGuid().ToString('N'))
$normalRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Winser'))
$serverRoot = Join-Path $profileRoot 'http'
$serverProcess = $null
$app = $null
$main = $null
$testWindowHandle = [IntPtr]::Zero
$desktop = $null
$smokeFailed = $false

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
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinserSmokeNative {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    public static int ForegroundProcessId() {
        uint processId;
        GetWindowThreadProcessId(GetForegroundWindow(), out processId);
        return (int)processId;
    }
    public static int WindowProcessId(IntPtr hWnd) {
        uint processId;
        GetWindowThreadProcessId(hWnd, out processId);
        return (int)processId;
    }
    public static void ActivateWindow(IntPtr hWnd) {
        uint processId;
        uint targetThread = GetWindowThreadProcessId(hWnd, out processId);
        uint currentThread = GetCurrentThreadId();
        bool attached = targetThread != 0 && currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try {
            ShowWindow(hWnd, 9);
            BringWindowToTop(hWnd);
            SetActiveWindow(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally {
            if (attached) AttachThreadInput(currentThread, targetThread, false);
        }
    }
}
'@

    function Save-WindowScreenshot($element, $path) {
        $bounds = $element.Current.BoundingRectangle
        if ($bounds.IsEmpty -or [double]::IsInfinity($bounds.Left) -or [double]::IsInfinity($bounds.Top)) { return }
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
    $bunShim = (Get-Command bun.exe -ErrorAction Stop | Select-Object -First 1).Source
    if (-not $bunShim) { throw 'A direct bun.exe installation is required for the UI smoke server.' }
    $bunRuntime = (& $bunShim -e 'console.log(process.execPath)' | Select-Object -Last 1).Trim()
    if (-not $bunRuntime -or -not (Test-Path -LiteralPath $bunRuntime)) {
        throw "Could not resolve Bun's real runtime executable from $bunShim."
    }
    $bun = [IO.Path]::GetFullPath($bunRuntime)
    $htmlPath = Join-Path $serverRoot 'index.html'
    $serverScript = Join-Path $serverRoot 'server.js'
    @'
const port = Number(Bun.argv[2]);
const page = Bun.argv[3];
Bun.serve({
  hostname: "127.0.0.1",
  port,
  fetch(request) {
    if (new URL(request.url).pathname === "/") {
      return new Response(Bun.file(page), { headers: { "content-type": "text/html; charset=utf-8" } });
    }
    return new Response("Not found", { status: 404 });
  },
});
'@ | Set-Content -LiteralPath $serverScript -Encoding utf8
    $serverProcess = Start-Process -FilePath $bun -ArgumentList @($serverScript, "$port", $htmlPath) -PassThru -WindowStyle Hidden
    $serverInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$($serverProcess.Id)" -ErrorAction SilentlyContinue
    if ($null -eq $serverInfo -or $serverInfo.Name -ne 'bun.exe' -or
        $serverInfo.CommandLine -notlike "*$serverScript*") {
        throw "Bun did not start the expected isolated fixture script: $($serverInfo.CommandLine)"
    }
    $url = "http://127.0.0.1:$port/"
    Start-Sleep -Milliseconds 500
    $serverProcess.Refresh()
    if ($serverProcess.HasExited) { throw 'The isolated Bun loopback server exited before the UI smoke started.' }

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
    $ownedProcessIds = [Collections.Generic.HashSet[int]]::new()
    [void] $ownedProcessIds.Add($app.Id)
    $processes = @(Get-CimInstance Win32_Process)
    do {
        $addedChild = $false
        foreach ($process in $processes) {
            if ($ownedProcessIds.Contains([int]$process.ParentProcessId) -and
                $ownedProcessIds.Add([int]$process.ProcessId)) { $addedChild = $true }
        }
    } while ($addedChild)
    $main = $null
    for ($i = 0; $i -lt 80 -and $null -eq $main; $i++) {
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $main = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        }
        else { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $main) { throw 'Winser did not create a main window.' }
    $testWindowHandle = [IntPtr] $main.Current.NativeWindowHandle
    if ($testWindowHandle -eq [IntPtr]::Zero) { throw 'Winser UIA main window has no native handle.' }
    $main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState(
        [Windows.Automation.WindowVisualState]::Normal)
    if ([WinserSmokeNative]::WindowProcessId($testWindowHandle) -ne $app.Id) {
        throw 'Winser main window handle does not belong to the isolated app process.'
    }
    [WinserSmokeNative]::ActivateWindow($testWindowHandle)
    $main.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Resize(800, 420)
    $windowBounds = $null
    for ($i = 0; $i -lt 40; $i++) {
        $windowBounds = $main.Current.BoundingRectangle
        if (-not $windowBounds.IsEmpty -and $windowBounds.Left -gt -10000 -and
            $windowBounds.Width -le 900 -and $windowBounds.Height -le 600) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($windowBounds.IsEmpty -or $windowBounds.Left -le -10000 -or $windowBounds.Width -gt 900 -or $windowBounds.Height -gt 600) {
        throw "Could not resize the app window for overflow checks: $windowBounds"
    }
    Save-WindowScreenshot $main $wideScreenshotPath

    function Find-Ui($name, $type = $null, $visibleOnly = $false) {
        $roots = @($main) + @($desktop.FindAll([Windows.Automation.TreeScope]::Children, $processCondition))
        foreach ($root in $roots) {
            if ($null -eq $root) { continue }
            $queue = [Collections.Generic.Queue[object]]::new()
            $queue.Enqueue(@($root, 0))
            while ($queue.Count -gt 0) {
                $entry = $queue.Dequeue()
                $element = $entry[0]
                $depth = $entry[1]
                if ($depth -ge 12) { continue }
                $current = $element.Current
                if ($current.Name -eq $name -and ($null -eq $type -or $current.ControlType -eq $type) -and
                    (-not $visibleOnly -or -not $current.IsOffscreen)) { return $element }
                foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Children,
                        [Windows.Automation.Condition]::TrueCondition)) {
                    $queue.Enqueue(@($child, $depth + 1))
                }
            }
        }
        return $null
    }

    function Find-UiType($type, $visibleOnly = $false) {
        $roots = @($main) + @($desktop.FindAll([Windows.Automation.TreeScope]::Children, $processCondition))
        foreach ($root in $roots) {
            if ($null -eq $root) { continue }
            $queue = [Collections.Generic.Queue[object]]::new()
            $queue.Enqueue(@($root, 0))
            while ($queue.Count -gt 0) {
                $entry = $queue.Dequeue()
                $element = $entry[0]
                $depth = $entry[1]
                if ($depth -ge 12) { continue }
                $current = $element.Current
                if ($current.ControlType -eq $type -and (-not $visibleOnly -or -not $current.IsOffscreen)) { return $element }
                foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Children,
                        [Windows.Automation.Condition]::TrueCondition)) {
                    $queue.Enqueue(@($child, $depth + 1))
                }
            }
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

    function Wait-Focused($name, $type = $null) {
        for ($i = 0; $i -lt 40; $i++) {
            $focused = [Windows.Automation.AutomationElement]::FocusedElement
            if ($null -ne $focused -and $focused.Current.Name -eq $name -and
                ($null -eq $type -or $focused.Current.ControlType -eq $type)) { return $focused }
            Start-Sleep -Milliseconds 250
        }
        $actual = [Windows.Automation.AutomationElement]::FocusedElement
        throw "Focus did not move to ${name}:$type (actual $($actual.Current.Name):$($actual.Current.ControlType.ProgrammaticName))."
    }

    function Resize-TestWindow([int] $width, [int] $height) {
        $main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState(
            [Windows.Automation.WindowVisualState]::Normal)
        $main.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Resize($width, $height)
        for ($i = 0; $i -lt 40; $i++) {
            $bounds = $main.Current.BoundingRectangle
            if ($bounds.Width -le ($width + 20) -and $bounds.Width -ge ($width - 20) -and
                $bounds.Height -le ($height + 20) -and $bounds.Height -ge ($height - 20)) {
                Ensure-AppForeground
                return
            }
            Start-Sleep -Milliseconds 250
        }
        throw "Window did not resize to ${width}x${height}: $($main.Current.BoundingRectangle)"
    }

    function Wait-Setting($name, $expected) {
        $settingsPath = Join-Path $profileRoot 'Data\settings.json'
        for ($i = 0; $i -lt 40; $i++) {
            if (Test-Path -LiteralPath $settingsPath) {
                try {
                    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                    if ($settings.$name -eq $expected) { return }
                }
                catch { }
            }
            Start-Sleep -Milliseconds 250
        }
        throw "Settings file did not contain $name=$expected."
    }

    function Ensure-AppForeground {
        $handle = $testWindowHandle
        if ([WinserSmokeNative]::WindowProcessId($handle) -ne $app.Id) {
            throw 'Refusing to activate a window outside the isolated test app process.'
        }
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $currentPid = [WinserSmokeNative]::ForegroundProcessId()
            if ($ownedProcessIds.Contains($currentPid)) { return }

            # UIA focus targets only the owned test window; then request activation and verify it.
            try { $main.SetFocus() } catch { }
            [WinserSmokeNative]::ActivateWindow($handle)
            Start-Sleep -Milliseconds 250
        }
        $foregroundPid = [WinserSmokeNative]::ForegroundProcessId()
        if (-not $ownedProcessIds.Contains($foregroundPid)) { throw "Refusing to send keys; foreground pid $foregroundPid is outside the isolated test app." }
    }

    function Move-PointerTo($element) {
        $bounds = $element.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) { return }
        [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
            [int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    }

    function Press-Enter($element) {
        Ensure-AppForeground
        Move-PointerTo $element
        $element.SetFocus()
        Send-AppKeys '{ENTER}'
    }

    function Invoke-Ui($element) {
        try { $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
        catch {
            Ensure-AppForeground
            $element.SetFocus()
            Send-AppKeys ' '
        }
    }

    function Send-AppKeys([string] $keys) {
        $foregroundPid = [WinserSmokeNative]::ForegroundProcessId()
        if (-not $ownedProcessIds.Contains($foregroundPid)) { throw "Refusing to send keys; foreground pid $foregroundPid is outside the isolated test app." }
        [Windows.Forms.SendKeys]::SendWait($keys)
    }

    function Save-UiTree($desktopRoot, $processIds, $path) {
        $lines = [Collections.Generic.List[string]]::new()
        foreach ($window in $desktopRoot.FindAll([Windows.Automation.TreeScope]::Children,
                [Windows.Automation.Condition]::TrueCondition)) {
            $current = $window.Current
            if (-not $processIds.Contains([int]$current.ProcessId)) { continue }
            $lines.Add(("ROOT pid={0} type={1} name=<{2}> bounds={3}" -f
                $current.ProcessId, $current.ControlType.ProgrammaticName, $current.Name, $current.BoundingRectangle))
            $queue = [Collections.Generic.Queue[object]]::new()
            $queue.Enqueue(@($window, 0))
            while ($queue.Count -gt 0) {
                $entry = $queue.Dequeue()
                $element = $entry[0]
                $depth = $entry[1]
                if ($depth -ge 7) { continue }
                $elementCurrent = $element.Current
                $lines.Add(("  {0}{1} type={2} focusable={3} focused={4} offscreen={5} bounds={6}" -f
                    (' ' * $depth), $elementCurrent.Name, $elementCurrent.ControlType.ProgrammaticName,
                    $elementCurrent.IsKeyboardFocusable, $elementCurrent.HasKeyboardFocus,
                    $elementCurrent.IsOffscreen, $elementCurrent.BoundingRectangle))
                foreach ($child in $element.FindAll([Windows.Automation.TreeScope]::Children,
                        [Windows.Automation.Condition]::TrueCondition)) {
                    $queue.Enqueue(@($child, $depth + 1))
                }
            }
        }
        $lines | Set-Content -LiteralPath $path -Encoding utf8
    }

    Ensure-AppForeground

    # The app's WebView profile directory is the in-process proof that it resolved our override.
    for ($i = 0; $i -lt 40 -and -not (Test-Path (Join-Path $profileRoot 'Profile')); $i++) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path (Join-Path $profileRoot 'Profile'))) {
        throw "Winser did not create its WebView profile under the isolated root: $profileRoot"
    }
    $showTabs = Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    $address = Wait-Ui 'Address and search' ([Windows.Automation.ControlType]::Edit)

    # Exercise navigation and the site info/menu controls using only the disposable profile.
    Ensure-AppForeground
    $address.SetFocus()
    Send-AppKeys '^a'
    $bareAddress = "127.0.0.1:$port/"
    Send-AppKeys $bareAddress
    $enteredValue = $address.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($enteredValue -ne $bareAddress) { throw "Address box did not contain the typed bare host: $enteredValue" }
    Send-AppKeys '{ENTER}'
    Wait-Ui 'Site information' ([Windows.Automation.ControlType]::Button) | Out-Null

    # The tab title confirms WebView2 loaded the loopback page, not just the address input.
    $showTabs = Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button)
    Press-Enter (Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button))
    Wait-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true | Out-Null
    Wait-Ui 'Winser smoke page' ([Windows.Automation.ControlType]::ListItem) $true | Out-Null
    $resolvedValue = (Wait-Ui 'Address and search' ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($resolvedValue -ne $url) { throw "Bare host did not resolve to $url (address value: $resolvedValue)." }
    $siteInfo = Wait-Ui 'Site information' ([Windows.Automation.ControlType]::Button)
    $siteInfo.SetFocus()
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Wait-UiGone 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true

    Press-Enter $siteInfo
    Wait-Ui '127.0.0.1' $null $true | Out-Null
    Send-AppKeys '{ESC}'

    # Ctrl+T, Ctrl+L, and F11 must leave keyboard focus in the actual address Edit.
    Send-AppKeys '^t'
    Wait-Focused 'Address and search' ([Windows.Automation.ControlType]::Edit) | Out-Null
    $address = Find-Ui 'Address and search' ([Windows.Automation.ControlType]::Edit)
    Send-AppKeys '^a'
    $bareAddress = "127.0.0.1:$port/"
    Send-AppKeys $bareAddress
    if ($address.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $bareAddress) {
        throw 'Typing after Ctrl+T did not reach the visible address Edit.'
    }
    Send-AppKeys '{ESC}'
    Send-AppKeys '^l'
    Wait-Focused 'Address and search' ([Windows.Automation.ControlType]::Edit) | Out-Null
    Send-AppKeys '{F11}'
    Start-Sleep -Milliseconds 500
    Send-AppKeys '^l'
    Wait-Focused 'Address and search' ([Windows.Automation.ControlType]::Edit) | Out-Null
    Send-AppKeys '{ESC}'
    Send-AppKeys '^w'

    $menu = Wait-Ui 'Settings and more' ([Windows.Automation.ControlType]::Button)
    Press-Enter $menu
    Wait-Ui 'Settings' ([Windows.Automation.ControlType]::MenuItem) $true | Out-Null
    Send-AppKeys '{ESC}'

    # Give the pane enough tabs to scroll, then verify keyboard selection stays visible.
    for ($i = 0; $i -lt 12; $i++) { Send-AppKeys '^t'; Start-Sleep -Milliseconds 150 }

    # Open the unpinned pane, verify focus keeps it open after pointer exit, then verify focus-out closes it.
    Press-Enter (Wait-Ui 'Show tabs' ([Windows.Automation.ControlType]::Button))
    Wait-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true | Out-Null
    $list = Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true
    $itemCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::ListItem)
    $listItems = $list.FindAll([Windows.Automation.TreeScope]::Descendants, $itemCondition)
    if ($listItems.Count -eq 0) { throw 'The vertical tab list has no realized items.' }
    $scroll = $list.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    Send-AppKeys '{HOME}'
    Start-Sleep -Milliseconds 250
    $startScroll = $scroll.Current.VerticalScrollPercent
    $selection = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)
    $firstSelection = $selection.Current.GetSelection()
    if ($firstSelection.Count -eq 0) { throw 'Home did not select a tab.' }
    $firstSelectionId = [string]::Join(',', $firstSelection[0].GetRuntimeId())
    Send-AppKeys '{END}'
    Start-Sleep -Milliseconds 400
    $selected = $selection.Current.GetSelection()
    if ($selected.Count -eq 0 -or $firstSelection.Count -eq 0 -or
        [string]::Join(',', $selected[0].GetRuntimeId()) -eq $firstSelectionId -or $selected[0].Current.IsOffscreen) {
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
    Send-AppKeys '{DOWN}'
    Send-AppKeys '{UP}'
    Start-Sleep -Milliseconds 300
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'Keyboard arrow navigation closed the focused vertical tab pane.'
    }
    Send-AppKeys '{ESC}'
    Wait-UiGone 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true
    Wait-Focused 'Address and search' ([Windows.Automation.ControlType]::Edit) | Out-Null

    # Pinning must keep the pane open after the pointer exits.
    $menu = Find-Ui 'Settings and more' ([Windows.Automation.ControlType]::Button)
    Press-Enter $menu
    $pin = Wait-Ui 'Pin the tab pane open' ([Windows.Automation.ControlType]::MenuItem) $true
    Invoke-Ui $pin
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Right - 10,
        [Windows.Forms.Screen]::PrimaryScreen.Bounds.Bottom - 10)
    Start-Sleep -Milliseconds 800
    if ($null -eq (Find-Ui 'Vertical tabs' ([Windows.Automation.ControlType]::List) $true)) {
        throw 'Pinning did not keep the vertical tab pane open after pointer exit.'
    }

    # A second row should preserve a usable address field at narrow widths.
    Resize-TestWindow 480 420
    $narrowAddress = Wait-Ui 'Address and search' ([Windows.Automation.ControlType]::Edit)
    $narrowBounds = $narrowAddress.Current.BoundingRectangle
    if ($narrowBounds.Width -lt 180 -or $narrowBounds.IsEmpty -or $narrowAddress.Current.IsOffscreen) {
        throw "Address field is not usable at 480px window width: $narrowBounds"
    }
    foreach ($name in @('Forward', 'New tab', 'Pin the tab pane open', 'Unpin the tab pane')) {
        if ($null -ne (Find-Ui $name ([Windows.Automation.ControlType]::Button) $true)) {
            throw "Compact toolbar control '$name' remained visible at 480px."
        }
    }
    Send-AppKeys '^f'
    Wait-Ui 'Find on page' $null $true | Out-Null
    $findWindow = $main.Current.BoundingRectangle
    foreach ($name in @('Match case', 'Previous match', 'Next match', 'Close find')) {
        $findControl = Wait-Ui $name ([Windows.Automation.ControlType]::Button) $true
        $bounds = $findControl.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Left -lt $findWindow.Left -or $bounds.Right -gt $findWindow.Right -or
            $bounds.Top -lt $findWindow.Top -or $bounds.Bottom -gt $findWindow.Bottom) {
            throw "Find control '$name' is outside the 480px window: $bounds"
        }
    }
    Press-Enter (Find-Ui 'Close find' ([Windows.Automation.ControlType]::Button) $true)
    Save-WindowScreenshot $main $narrowScreenshotPath
    Resize-TestWindow 1100 700

    # Open the Appearance settings page and exercise both theme choices in the isolated profile.
    $menu = Find-Ui 'Settings and more' ([Windows.Automation.ControlType]::Button)
    Press-Enter $menu
    $settingsItem = Wait-Ui 'Settings' ([Windows.Automation.ControlType]::MenuItem) $true
    Invoke-Ui $settingsItem
    Wait-Ui 'Appearance' ([Windows.Automation.ControlType]::Text) $true | Out-Null
    Wait-Setting 'theme' 0
    $themeCombo = Wait-Ui 'Theme' ([Windows.Automation.ControlType]::ComboBox) $true
    $expand = $themeCombo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $lightOption = Wait-Ui 'Light' $null $true
    $optionPatterns = @($lightOption.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ', '
    Write-Output "Theme Light UIA peer: $($lightOption.Current.ControlType.ProgrammaticName); patterns=[$optionPatterns]"
    $lightPattern = $null
    if ($lightOption.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$lightPattern)) {
        $lightPattern.Select()
    }
    else { Invoke-Ui $lightOption }
    Wait-Setting 'theme' 1
    $expand.Expand()
    $darkOption = Wait-Ui 'Dark' $null $true
    $darkPattern = $null
    if ($darkOption.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$darkPattern)) {
        $darkPattern.Select()
    }
    else { Invoke-Ui $darkOption }
    Wait-Setting 'theme' 2

    # Verify the responsive horizontal mode and keyboard address entry, then restore vertical mode.
    $verticalToggle = Wait-Ui 'Show tabs on the side' ([Windows.Automation.ControlType]::Button) $true
    $togglePattern = $verticalToggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($togglePattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $togglePattern.Toggle() }
    Wait-Setting 'useVerticalTabs' $false
    Send-AppKeys '^t'
    Wait-Focused 'Address and search' ([Windows.Automation.ControlType]::Edit) | Out-Null
    $horizontalAddress = Find-Ui 'Address and search' ([Windows.Automation.ControlType]::Edit)
    Send-AppKeys '^a'
    $bareAddress = "127.0.0.1:$port/"
    Send-AppKeys $bareAddress
    if ($horizontalAddress.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $bareAddress) {
        throw 'Typing after Ctrl+T did not reach the horizontal address edit.'
    }
    Send-AppKeys '{ENTER}'
    Wait-Ui 'Winser smoke page' ([Windows.Automation.ControlType]::TabItem) $true | Out-Null
    Send-AppKeys '^w'
    $verticalToggle = Wait-Ui 'Show tabs on the side' ([Windows.Automation.ControlType]::Button) $true
    $togglePattern = $verticalToggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($togglePattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) { $togglePattern.Toggle() }
    Wait-Setting 'useVerticalTabs' $true
    Save-WindowScreenshot $main $settingsScreenshotPath

    Write-Output "UI smoke passed: isolated profile $profileRoot; loopback page $url"
    Write-Output "800px screenshot: $wideScreenshotPath"
    Write-Output "Narrow-window screenshot: $narrowScreenshotPath"
    Write-Output "Appearance settings screenshot: $settingsScreenshotPath"
}
catch {
    $smokeFailed = $true
    throw
}
finally {
    if ($null -ne $main) {
        try {
            Save-WindowScreenshot $main $screenshotPath
            Write-Output "UI screenshot: $screenshotPath"
        }
        catch { Write-Warning "Could not save UI screenshot: $_" }
    }
    if ($smokeFailed -and $null -ne $app -and $null -ne $desktop) {
        $treePath = Join-Path $screenshotRoot ("winser-uia-{0}.txt" -f $app.Id)
        try {
            Save-UiTree $desktop $ownedProcessIds $treePath
            Write-Output "UI Automation snapshot: $treePath"
        }
        catch { Write-Warning "Could not save UI Automation snapshot: $_" }
    }
    if ($null -ne $app -and -not $app.HasExited) {
        [void] $app.CloseMainWindow()
        if (-not $app.WaitForExit(10000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        $serverInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$($serverProcess.Id)" -ErrorAction SilentlyContinue
        if ($null -ne $serverInfo -and $serverInfo.Name -eq 'bun.exe' -and
            $serverInfo.CommandLine -like "*$serverScript*") {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        }
        else { Write-Warning "Left Bun PID $($serverProcess.Id) running because it did not match this smoke's fixture script." }
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
