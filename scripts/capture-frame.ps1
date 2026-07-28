# Capture the Space Engineers 2 window to a PNG.
#
# WHY NOT A GPU READBACK. The obvious way to "grab the feed" is to copy our LDR texture
# into a readback buffer inside the render thread and encode it. That adds a new class of
# GPU work to a render path that has crashed repeatedly today, for a debugging
# convenience. This costs the game nothing: it is an ordinary window capture, entirely
# outside the process.
#
# It is also MORE useful. Every bug still open is in the PLAYER'S view — the phantom
# images on the walls, the shimmer on fine detail. A capture of the game window shows
# those AND the panel showing the feed, in one image, correctly related to each other.
# A readback of our own texture would show only the half that is working.
#
# Captures the SE2 window specifically rather than the whole desktop: more useful framing,
# and it does not sweep up whatever else is on the user's screen.
#
#   .\capture-frame.ps1                      -> output/frames/frame-<timestamp>.png
#   .\capture-frame.ps1 -Label bleed         -> output/frames/bleed-<timestamp>.png
#   .\capture-frame.ps1 -Count 5 -DelayMs 2000
#
# -Count with -DelayMs is how to catch something intermittent, or to see whether an
# artefact updates in discrete steps.

param(
    [string]$Label = "frame",
    [int]$Count = 1,
    [int]$DelayMs = 0,
    [string]$OutDir = ""
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if ([string]::IsNullOrEmpty($OutDir)) {
    $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) "output\frames"
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

# Window bounds via Win32. GetClientRect + ClientToScreen would drop the title bar, but
# GetWindowRect is enough here and is far less fiddly.
$sig = @'
using System;
using System.Runtime.InteropServices;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
if (-not ([System.Management.Automation.PSTypeName]'Win32Cap').Type) {
    Add-Type -TypeDefinition $sig
}

$proc = Get-Process -Name "SpaceEngineers2" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

if ($null -eq $proc) {
    Write-Output "NO_GAME: SpaceEngineers2 is not running (or has no window)."
    exit 2
}

$h = $proc.MainWindowHandle
if ([Win32Cap]::IsIconic($h)) {
    Write-Output "MINIMISED: the game window is minimised; a capture would be blank."
    exit 3
}

$r = New-Object Win32Cap+RECT
if (-not [Win32Cap]::GetWindowRect($h, [ref]$r)) {
    Write-Output "NO_BOUNDS: could not read the window rectangle."
    exit 4
}

$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top
if ($w -le 0 -or $hgt -le 0) {
    Write-Output "BAD_BOUNDS: ${w}x${hgt}"
    exit 5
}

for ($i = 0; $i -lt $Count; $i++) {
    if ($i -gt 0 -and $DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hgt)))
        $stamp = (Get-Date).ToString("HHmmss-fff")
        $path = Join-Path $OutDir "$Label-$stamp.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "OK: $path (${w}x${hgt})"
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}
