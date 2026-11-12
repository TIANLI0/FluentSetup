# UI Automation helpers for driving the installer front ends (WinUI and Win32) in tests.
# WinUI controls expose UIA patterns directly. For classic Win32 controls the managed .NET Framework UIA client has no
# proxies (it reports them as "Pane"; native MSAA/UIA - what Narrator uses - reports the correct roles), so the helpers
# fall back to the control's window handle and standard button messages.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('FsUia.Win32' -as [type])) {
    Add-Type -Namespace FsUia -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
}

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$BM_GETCHECK = 0x00F0; $BM_CLICK = 0x00F5

function Get-UiaWindow {
    param([Parameter(Mandatory)][int] $ProcessId, [int] $TimeoutSeconds = 20)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $w = $AE::RootElement.FindFirst($TS::Children, $cond)
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

Export-ModuleMember -Function Get-UiaWindow, Find-UiaElement, Invoke-UiaButton, Set-UiaToggle, Test-UiaElement, Wait-UiaElement
