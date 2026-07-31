# Which process owns the foreground window? Prints its name, or "?" if unresolvable.
#
# Exists because the game frame-caps itself while backgrounded (user-reported 2026-07-30),
# so every perf window needs a focus stamp to be trustworthy — and the engine does not log
# focus changes. A separate file rather than an inline powershell -Command because the
# inline form (bash single-quotes around C# in a PowerShell string) proved fragile: it
# worked on the first watchdog tick and returned empty on later ones, which is worse than
# not existing — an intermittent instrument invites reading its silence as AWAY.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FgWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
$fgPid = [uint32]0
[void][FgWin]::GetWindowThreadProcessId([FgWin]::GetForegroundWindow(), [ref]$fgPid)
try { (Get-Process -Id $fgPid -ErrorAction Stop).ProcessName } catch { "?" }
