<#
.SYNOPSIS
  Runs a command on a separate, invisible desktop at idle priority, so its windows never appear on the user's
  screen, never take focus or input, and it only uses spare CPU. Child processes inherit both.
  Output (stdout + stderr) goes to -Log. Returns the command's exit code.
.EXAMPLE
  .\run-hidden.ps1 -Command 'powershell -NoProfile -File tests\Integration\e2e.ps1' -WorkingDirectory D:\src -Log D:\e2e.log
#>
param(
    [Parameter(Mandatory)] [string] $Command,
    [string] $WorkingDirectory = (Get-Location).Path,
    [Parameter(Mandatory)] [string] $Log,
    [int] $TimeoutMinutes = 60,
    [string] $Desktop = 'FluentSetupTests'
)
$ErrorActionPreference = 'Stop'
if (-not ('HiddenDesktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class HiddenDesktop {
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                      IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static int Run(string desktop, string commandLine, string cwd, uint timeoutMs) {
        const uint DESKTOP_ALL = 0x01FF | 0x000F0000;
        IntPtr desk = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL, IntPtr.Zero);
        if (desk == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\" + desktop;
            const uint IDLE_PRIORITY_CLASS = 0x40, CREATE_NO_WINDOW = 0x08000000;
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                                IDLE_PRIORITY_CLASS | CREATE_NO_WINDOW, IntPtr.Zero, cwd, ref si, out pi))
                throw new System.ComponentModel.Win32Exception();
            try {
                if (WaitForSingleObject(pi.hProcess, timeoutMs) != 0) { TerminateProcess(pi.hProcess, 1460); return 1460; }
                uint code; GetExitCodeProcess(pi.hProcess, out code); return (int)code;
            } finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        } finally { CloseDesktop(desk); }
    }
}
'@
}
$full = "cmd.exe /d /s /c `"$Command > `"$Log`" 2>&1`""
[HiddenDesktop]::Run($Desktop, $full, $WorkingDirectory, [uint32]($TimeoutMinutes * 60000))
