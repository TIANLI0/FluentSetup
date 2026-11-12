<#
.SYNOPSIS
  Captures the top-level window of a process to a PNG (used by the UI checks in tests/UI).
  Uses PrintWindow(PW_RENDERFULLCONTENT) so only the target window is rendered, even when other windows overlap it.
.EXAMPLE
  scripts/capture-window.ps1 -ProcessId 1234 -Path out/shots/welcome.png
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $Path,
    [int] $TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'

if (-not ('FsCapture5.Native' -as [type])) {
    Add-Type -Language CSharp -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
namespace FsCapture5 {
public static class Native {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static IntPtr Find(uint pid) {
        IntPtr found = IntPtr.Zero; long bestArea = 0;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            if (sb.Length == 0) return true;
            RECT r; GetWindowRect(h, out r);
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; found = h; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    public static void Capture(IntPtr h, string path) {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels everywhere
        RECT win; GetWindowRect(h, out win);
        RECT vis; DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out vis, Marshal.SizeOf(typeof(RECT)));
        int w = win.R - win.L, hgt = win.B - win.T;
        using (var full = new Bitmap(w, hgt, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(full)) {
                IntPtr dc = g.GetHdc();
                PrintWindow(h, dc, 2 /*PW_RENDERFULLCONTENT*/);
                g.ReleaseHdc(dc);
            }
            // Crop the invisible resize borders so the image matches what the user sees.
            var crop = new Rectangle(vis.L - win.L, vis.T - win.T, vis.R - vis.L, vis.B - vis.T);
            crop.Intersect(new Rectangle(0, 0, w, hgt));
            if (crop.Width <= 0 || crop.Height <= 0) crop = new Rectangle(0, 0, w, hgt);
            using (var cut = full.Clone(crop, full.PixelFormat)) cut.Save(path, ImageFormat.Png);
        }
    }
}
}
'@
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$h = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    $h = [FsCapture5.Native]::Find([uint32]$ProcessId)
    if ($h -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
if ($h -eq [IntPtr]::Zero) { throw "No visible window for process $ProcessId" }
Start-Sleep -Milliseconds 900   # let entrance transitions settle
$dir = Split-Path -Parent $Path
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
[FsCapture5.Native]::Capture($h, (Resolve-Path -LiteralPath $dir).Path + '\' + (Split-Path -Leaf $Path))
[FsCapture5.Native]::Title($h)
