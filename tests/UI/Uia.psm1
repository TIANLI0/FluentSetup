# UI Automation helpers for driving the installer front ends (WinUI and Win32) in tests.
# WinUI controls expose UIA patterns directly. For classic Win32 controls the managed .NET Framework UIA client has no
# proxies (it reports them as "Pane"; native MSAA/UIA - what Narrator uses - reports the correct roles), so the helpers
# fall back to the control's window handle and standard button messages.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('FsUia.Win32' -as [type])) {
    Add-Type -Namespace FsUia -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageText(IntPtr h, uint m, IntPtr w, string l);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
[DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder name, int max);
public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString(); }
'@
}

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$BM_GETCHECK = 0x00F0; $BM_CLICK = 0x00F5
$WM_SETTEXT = 0x000C; $WM_COMMAND = 0x0111; $CB_SELECTSTRING = 0x014D; $CBN_SELCHANGE = 1; $GW_HWNDNEXT = 2

function Get-UiaWindow {
    param([Parameter(Mandatory)][int] $ProcessId, [int] $TimeoutSeconds = 20)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # The process can own other top-level windows (e.g. the input method's indicator overlay): prefer the
        # installer's own window classes, then a titled window.
        $windows = @($AE::RootElement.FindAll($TS::Children, $cond))
        $w = $windows | Where-Object { $_.Current.ClassName -in @('WinUIDesktopWin32WindowClass', 'FluentSetupFallbackWindow') } | Select-Object -First 1
        if (-not $w) { $w = $windows | Where-Object { $_.Current.Name } | Select-Object -First 1 }
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    }
    throw "No UIA window for process $ProcessId"
}

function Find-UiaElement {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [int] $TimeoutSeconds = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    while ((Get-Date) -lt $deadline) {
        foreach ($e in $Root.FindAll($TS::Descendants, $cond)) {
            if (-not $e.Current.IsOffscreen -and $e.Current.IsEnabled) { return $e }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Element '$Name' not found (or not enabled) within $TimeoutSeconds s"
}

function Invoke-UiaButton {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [int] $TimeoutSeconds = 15)
    $b = Find-UiaElement -Root $Root -Name $Name -TimeoutSeconds $TimeoutSeconds
    $pattern = $null
    if ($b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    $hwnd = [IntPtr]$b.Current.NativeWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw "'$Name' cannot be invoked" }
    [void][FsUia.Win32]::SendMessageW($hwnd, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
}

function Set-UiaToggle {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [bool] $On = $true)
    $c = Find-UiaElement -Root $Root -Name $Name
    $pattern = $null
    if ($c.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $want = if ($On) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
        for ($i = 0; $i -lt 3 -and $pattern.Current.ToggleState -ne $want; $i++) { $pattern.Toggle() }
        return
    }
    $hwnd = [IntPtr]$c.Current.NativeWindowHandle
    $checked = [int][FsUia.Win32]::SendMessageW($hwnd, $BM_GETCHECK, [IntPtr]::Zero, [IntPtr]::Zero) -eq 1
    if ($checked -ne $On) { [void][FsUia.Win32]::SendMessageW($hwnd, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) }
}

# A classic Win32 field: the control named after its label (MSAA takes the name from the static label before it), or
# the window right after that label.
function Get-Win32Field($elements, [string] $class) {
    foreach ($e in $elements) {
        $h = [IntPtr]$e.Current.NativeWindowHandle
        if ($h -ne [IntPtr]::Zero -and [FsUia.Win32]::ClassOf($h) -eq $class) { return $h }
    }
    foreach ($e in $elements) {
        $h = [IntPtr]$e.Current.NativeWindowHandle
        if ($h -eq [IntPtr]::Zero -or [FsUia.Win32]::ClassOf($h) -ne 'Static') { continue }
        $next = [FsUia.Win32]::GetWindow($h, $GW_HWNDNEXT)
        if ($next -ne [IntPtr]::Zero -and [FsUia.Win32]::ClassOf($next) -eq $class) { return $next }
    }
    return [IntPtr]::Zero
}

function Find-UiaAll($Root, [string] $Name, [int] $TimeoutSeconds = 15) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $found = @($Root.FindAll($TS::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
        if ($found.Count) { return $found }
        Start-Sleep -Milliseconds 200
    }
    throw "Element '$Name' not found within $TimeoutSeconds s"
}

# Types into a text or password field identified by its label (WinUI: ValuePattern; Win32: WM_SETTEXT, which also
# sends EN_CHANGE like typing does).
function Set-UiaText {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][AllowEmptyString()][string] $Value)
    $all = Find-UiaAll $Root $Name
    foreach ($e in $all) {
        $pattern = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern) -and -not $pattern.Current.IsReadOnly) {
            $pattern.SetValue($Value)
            return
        }
    }
    $edit = Get-Win32Field $all 'Edit'
    if ($edit -eq [IntPtr]::Zero) { throw "'$Name' is not a text field" }
    [void][FsUia.Win32]::SendMessageText($edit, $WM_SETTEXT, [IntPtr]::Zero, $Value)
}

# Picks an entry of a drop-down list identified by its label.
function Select-UiaChoice {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Item)
    $all = Find-UiaAll $Root $Name
    foreach ($e in $all) {
        $expand = $null
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { continue }
        $expand.Expand()
        try {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Item)
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                foreach ($candidate in @($e.FindAll($TS::Descendants, $cond)) + @($Root.FindAll($TS::Descendants, $cond))) {
                    $select = $null
                    if ($candidate.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) {
                        $select.Select()
                        return
                    }
                }
                Start-Sleep -Milliseconds 200
            }
            throw "'$Item' not found in '$Name'"
        } finally {
            try { $expand.Collapse() } catch { }
        }
    }
    $combo = Get-Win32Field $all 'ComboBox'
    if ($combo -eq [IntPtr]::Zero) { throw "'$Name' is not a drop-down list" }
    if ([int64][FsUia.Win32]::SendMessageText($combo, $CB_SELECTSTRING, [IntPtr](-1), $Item) -lt 0) { throw "'$Item' not found in '$Name'" }
    # CB_SELECTSTRING does not notify; tell the owner like a user's selection would.
    $id = [FsUia.Win32]::GetDlgCtrlID($combo)
    [void][FsUia.Win32]::SendMessageW([FsUia.Win32]::GetParent($combo), $WM_COMMAND, [IntPtr](($CBN_SELCHANGE -shl 16) -bor $id), $combo)
}

# Whether the visible element with this name is enabled ($null if there is none).
function Test-UiaEnabled {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $visible = @($Root.FindAll($TS::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
    # A WinUI button's caption is a text element of the same name inside it; the button itself decides.
    $button = $visible | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
    if ($button) { return $button.Current.IsEnabled }
    if ($visible.Count) { return $visible[0].Current.IsEnabled }
    return $null
}

function Test-UiaElement {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    foreach ($e in $Root.FindAll($TS::Descendants, $cond)) { if (-not $e.Current.IsOffscreen) { return $true } }
    return $false
}

function Wait-UiaElement {
    param([Parameter(Mandatory)] $Root, [Parameter(Mandatory)][string] $Name, [int] $TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-UiaElement -Root $Root -Name $Name) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for '$Name'"
}

Export-ModuleMember -Function Get-UiaWindow, Find-UiaElement, Invoke-UiaButton, Set-UiaToggle, Test-UiaElement, Wait-UiaElement, Set-UiaText,
    Select-UiaChoice, Test-UiaEnabled
