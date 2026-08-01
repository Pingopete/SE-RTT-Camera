# Real-time CTD watch for Space Engineers 2.
#
# WHY THIS EXISTS. Crashes were being noticed minutes late, by which time "what changed just
# before it died" was guesswork - and rtt.log carries NO DATES, so scrolling back for the
# moment of death has repeatedly matched lines from a PREVIOUS session. This samples once a
# second, stamps the transition with a wall clock, and writes the correlation window itself.
#
# It also solves the other half: a crashed SE2 is still a RUNNING PROCESS. The crash handler
# sits on a message box forever, so "is the process alive" says healthy while nothing works.
# The window TITLE is what tells the truth ("Application has crashed!").
#
# ASCII ONLY - a .ps1 saved as UTF-8 without a BOM has its non-ASCII characters misparsed by
# Windows PowerShell, which has already killed one script in this project.
#
#   powershell -File scripts\ctd-watch.ps1                 watch, log, force-close on crash
#   powershell -File scripts\ctd-watch.ps1 -Relaunch       ... and reload the last save
#   powershell -File scripts\ctd-watch.ps1 -NoKill         watch and log only
#
# Events land in output\ctd-events.log, newest last. Read that file, not the console.

param(
    [switch]$Relaunch,
    [switch]$NoKill,
    [int]$IntervalMs = 1000,
    [int]$CorrelationLines = 40
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$events = Join-Path $root 'output\ctd-events.log'
$rtt = Join-Path $root 'output\rtt.log'

function Note($text) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Add-Content -Path $events -Value $line -Encoding utf8
    Write-Output $line
}

function GameAny { Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Select-Object -First 1 }
function GameWin {
    Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

# THE CORRELATION WINDOW - the whole reason this script exists.
#
# rtt.log has no dates and spans every session, so "the last N lines" has repeatedly matched a
# PREVIOUS run. Captured at the instant of death instead, those lines are unambiguously the
# ones that were being written as it died. The build stamp goes in too, because the question
# is always "which build was live when this happened".
function Correlate {
    try {
        $stamp = (Select-String -Path $rtt -Pattern 'Logic loaded \(build stamp' -ErrorAction SilentlyContinue |
                  Select-Object -Last 1).Line
        if ($stamp) { Note "  build live at death: $($stamp.Trim())" }

        $tail = Get-Content $rtt -Tail $CorrelationLines -ErrorAction SilentlyContinue
        Note "  --- last $CorrelationLines rtt.log lines at time of death ---"
        foreach ($l in $tail) { Add-Content -Path $events -Value ("      " + $l) -Encoding utf8 }
        Note "  --- end correlation window ---"
    } catch { Note "  correlation failed: $_" }
}

Note "ctd-watch started (interval ${IntervalMs}ms, relaunch=$Relaunch, kill=$(-not $NoKill))"

# State: Absent | Alive | Crashed. Only TRANSITIONS are logged, so the file stays readable.
$state = 'Absent'
$aliveSince = $null

while ($true) {
    Start-Sleep -Milliseconds $IntervalMs

    $any = GameAny
    $win = GameWin
    $title = if ($win) { $win.MainWindowTitle } else { '' }

    $now = if (-not $any) { 'Absent' }
           elseif ($title -match 'crash') { 'Crashed' }
           else { 'Alive' }

    if ($now -eq $state) { continue }

    switch ($now) {
        'Alive' {
            $aliveSince = Get-Date
            Note "GAME UP (pid $($any.Id))"
        }
        'Absent' {
            if ($state -eq 'Alive') {
                # Vanished without a crash dialog: a hard kill, a device-removal exit, or the
                # user closing it. Still worth the correlation window - a silent disappearance
                # is exactly the case that used to go unexplained.
                $lived = if ($aliveSince) { [int]((Get-Date) - $aliveSince).TotalSeconds } else { -1 }
                Note "GAME GONE (no crash dialog; was up ${lived}s) -- correlation window follows"
                Correlate
            } else {
                Note "GAME GONE (from state $state)"
            }
        }
        'Crashed' {
            $lived = if ($aliveSince) { [int]((Get-Date) - $aliveSince).TotalSeconds } else { -1 }
            Note "*** CTD DETECTED *** (pid $($win.Id), window '$title', was up ${lived}s)"
            Correlate

            if (-not $NoKill) {
                # The dump is already written by the time the message box appears - the game's
                # own log orders it "Collecting crash dump" -> "Serializing crash meta" ->
                # "Waiting on message box confirmation" - so nothing is lost by closing it.
                Note "force-closing the crashed process"
                try { Stop-Process -Id $win.Id -Force -ErrorAction Stop } catch { Note "could not stop it: $_" }
                for ($i = 0; $i -lt 20 -and (GameAny); $i++) { Start-Sleep -Seconds 1 }
            }
            if ($Relaunch) {
                Note "relaunching via resume-after-crash.ps1"
                & (Join-Path $PSScriptRoot 'resume-after-crash.ps1') | ForEach-Object { Note "  resume: $_" }
            }
        }
    }
    $state = $now
}
