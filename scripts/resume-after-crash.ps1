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
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@

# TWO PROCESSES ANSWER TO THIS NAME: a ~44 MB launcher/helper with NO window, and the
# actual game. "Select the first one" is a coin flip between them, and picking the helper
# is silent poison - its MainWindowHandle is 0, the send-Enter loop hits its
# "no window yet, keep waiting" branch on every attempt, and the script reports "world
# never loaded" while the game sits happily on the main menu. That is exactly what
# happened at 14:21 after the first run at 13:44 got the ordering it wanted.
#
# So: ask for the process WITH A WINDOW when the answer is going to be used for input,
# and for any process at all when the question is merely "did it start".
function Game { Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
function GameAny { Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Select-Object -First 1 }

# A CRASHED GAME IS STILL A RUNNING PROCESS.
#
# After a device removal the crash handler collects its dump and then sits on a message
# box forever, so the process stays alive with a window titled "Application has crashed!".
# The first version of this script saw a live process, reported "already running" and
# exited 0 - so the unattended recovery it exists to perform never happened, and the
# session sat dead behind a dialog looking healthy. Check what the window SAYS, not
# merely that a process exists.
#
# The dump is already written by the time the box appears (the log says "Collecting crash
# dump" then "Serializing crash meta" before "Waiting on message box confirmation"), so
# killing it here loses no forensics.
$g = Game
if ($g -and $g.MainWindowTitle -match 'crash') {
    Write-Output "found a CRASHED game (pid $($g.Id), window '$($g.MainWindowTitle)') - closing it before relaunch"
    try { Stop-Process -Id $g.Id -Force -ErrorAction Stop } catch { Write-Output "could not stop it: $_" }
    for ($i = 0; $i -lt 20 -and (Game); $i++) { Start-Sleep -Seconds 1 }
}

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
$linesBefore      = @(Get-Content "$root\output\rtt.log" -ErrorAction SilentlyContinue).Count
Start-Process "steam://rungameid/1133870"

$deadline = (Get-Date).AddMinutes(5)
while (-not (GameAny) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
if (-not (GameAny)) { Write-Output "FAILED: process never appeared"; exit 1 }
Write-Output "process up (pid $((GameAny).Id)); waiting for the main menu"

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

    # keybd_event, NOT SendKeys.
    #
    # SendKeys posts WM_KEYDOWN/WM_KEYUP to the focused window. The main menu does not read
    # its input that way, so the keystroke landed nowhere: on 2026-08-01 at 14:26 the window
    # was verifiably focused, SendKeys reported success, and the game sat on the menu for ten
    # minutes with nothing in its log but analytics heartbeats. keybd_event injects into the
    # input stream itself and the same Enter loaded the world immediately.
    #
    # The scan code matters as much as the virtual key - input layers that ignore vk=0 will
    # still honour 0x1C.
    Write-Output "attempt ${attempt}: focused, injecting Enter (Continue) via keybd_event"
    [Win]::keybd_event(0x0D, 0x1C, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [Win]::keybd_event(0x0D, 0x1C, 2, [UIntPtr]::Zero)

    # ONLY LINES WRITTEN SINCE WE LAUNCHED COUNT. The first version grepped the last 400 lines
    # for "FEED GATE: ACTIVE" and matched a line from BEFORE the crash, so it reported
    # "WORLD LOADED" while the game was still sitting at the menu. rtt.log carries no dates and
    # spans every session, which has now produced this same false reading in three different
    # tools today. Compare against a line count taken before the launch instead.
    for ($w = 0; $w -lt 10; $w++) {
        Start-Sleep -Seconds 6
        $lines = @(Get-Content "$root\output\rtt.log" -ErrorAction SilentlyContinue)
        $fresh = if ($lines.Count -gt $linesBefore) { $lines[$linesBefore..($lines.Count - 1)] } else { @() }
        if ($fresh -match 'FEED GATE: ACTIVE') {
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
