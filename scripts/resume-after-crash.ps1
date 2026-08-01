# Relaunch SE2 after a crash and load the last save, unattended.
#
# Authorised by the user 2026-08-01 for exactly this: launch the exe, then press Enter at the
# main menu (which activates Continue and loads the test grid on the planet). Nothing else is
# automated - this script clicks no other button and types nothing else.
#
# It is deliberately conservative about the one keystroke it sends: it focuses the game window
# first and verifies it HAS focus before sending, so the Enter cannot land in another
# application. If focus cannot be taken it gives up rather than typing blind.
#
# ASCII ONLY, on purpose. A .ps1 saved as UTF-8 without a BOM has its non-ASCII characters
# misparsed by Windows PowerShell, and the first version of this file died with "the string is
# missing the terminator" because of em-dashes inside comments.
#
#   powershell -File scripts\resume-after-crash.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
'@

function Game { Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Select-Object -First 1 }

if (Game) { Write-Output "already running (pid $((Game).Id))"; exit 0 }

# The crash left *-live.marker files behind. That latch is correct after a real death, but the
# cause of this one is understood and fixed, so clear them rather than boot with the feed
# disabled and spend the session wondering why the panel is black.
Get-ChildItem "$root\output\*-live.marker" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output "clearing stale latch: $($_.Name)"
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}

# LAUNCH EXACTLY AS THE DESKTOP SHORTCUT DOES.
#
# Two wrong ways were tried first, both worth recording:
#
#   launch-se2.bat runs the exe directly. Fine by hand, because Steam is already the parent.
#   Unattended it is not: the exe sees it was not started by Steam, logs "Game not started
#   through steam. Closing it and asking steam to start it", exits 0, and Steam relaunches it
#   WITHOUT the -plugins: argument. The mod never loads and the session looks alive but is
#   useless. launch-se2.bat's own footer warned about this.
#
#   steam://run/<appid>//<args> does attach the argument, but Steam then raises a confirmation
#   dialog for custom arguments. The Enter meant for the main menu dismissed THAT instead, so
#   the first successful-looking run worked by accident.
#
# steam://rungameid/<appid> is what the desktop shortcut uses: Steam applies its own configured
# launch options, no dialog, no bounce. App 1133870 = Space Engineers 2.
Write-Output "launching via steam://rungameid/1133870 (as the desktop shortcut does)"
$bootstrapsBefore = (Select-String -Path "$root\output\rtt.log" -Pattern 'RttProbe bootstrap' -ErrorAction SilentlyContinue | Measure-Object).Count
Start-Process "steam://rungameid/1133870"

$deadline = (Get-Date).AddMinutes(5)
while (-not (Game) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
if (-not (Game)) { Write-Output "FAILED: process never appeared"; exit 1 }
Write-Output "process up (pid $((Game).Id)); waiting for the main menu"

# No reliable log marker for "menu ready", so retry rather than try to time it. Enter at the
# wrong moment is harmless - the menu ignores it.
for ($attempt = 1; $attempt -le 12; $attempt++) {
    Start-Sleep -Seconds 20
    # A MISSING PROCESS IS NOT YET A FAILURE. Steam can restart the exe during startup, so the
    # process legitimately disappears and comes back. The first version treated one absent
    # sample as fatal and gave up while the game was still coming up.
    $p = Game
    if (-not $p) { Write-Output "attempt ${attempt}: process not present (Steam may be restarting it), waiting"; continue }
    if ($p.MainWindowHandle -eq 0) { continue }

    [void][Win]::ShowWindow($p.MainWindowHandle, 9)
    [void][Win]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Milliseconds 600

    if ([Win]::GetForegroundWindow() -ne $p.MainWindowHandle) {
        Write-Output "attempt ${attempt}: could not focus the game window, NOT sending Enter"
        continue
    }

    Write-Output "attempt ${attempt}: focused, sending Enter (Continue)"
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

    for ($w = 0; $w -lt 10; $w++) {
        Start-Sleep -Seconds 6
        $tail = Get-Content "$root\output\rtt.log" -Tail 400 -ErrorAction SilentlyContinue
        if ($tail -match 'FEED GATE: ACTIVE') {
            # Confirm the PLUGIN loaded, not merely that a world did. If Steam's launch options
            # ever lose the -plugins: argument the game comes up perfectly and the mod is simply
            # absent - which reads as "everything is fine" right up until nothing is measurable.
            $after = (Select-String -Path "$root\output\rtt.log" -Pattern 'RttProbe bootstrap' -ErrorAction SilentlyContinue | Measure-Object).Count
            if ($after -le $bootstrapsBefore) {
                Write-Output "WARNING: world loaded but NO new RttProbe bootstrap - the plugin did not load. Check Steam's launch options for -plugins:"
                exit 2
            }
            Write-Output "WORLD LOADED (feed gate active, plugin bootstrapped)"
            exit 0
        }
    }
    Write-Output "attempt ${attempt}: no world yet, retrying"
}
Write-Output "FAILED: world never loaded"
exit 1
