# Which plugins did SE2 actually load, and are they alive?
#
# Steam launch options override the -plugins: argument passed by launch-se2.bat,
# so "I launched with the bat" is not evidence that a plugin loaded. This reads
# the real command line off the running process and checks each mod's log.

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== SE2 processes ===" -ForegroundColor Cyan
$procs = Get-CimInstance Win32_Process -Filter "Name='SpaceEngineers2.exe'"
if (-not $procs) {
    Write-Host "  SE2 is not running." -ForegroundColor Yellow
} else {
    foreach ($p in $procs) {
        Write-Host "  PID $($p.ProcessId)"
        $cmd = $p.CommandLine
        if ($cmd -match '-plugins:(\S+)') {
            foreach ($dll in ($Matches[1].Trim('"') -split ';')) {
                $exists = if (Test-Path $dll) { "OK" } else { "MISSING" }
                Write-Host "      plugin: $dll  [$exists]"
            }
        } else {
            Write-Host "      no -plugins: argument" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "=== Mod logs ===" -ForegroundColor Cyan

$logs = [ordered]@{
    'RTT Camera'       = 'D:\Projects\Space Engineers Stuff\RTT Camera\output\rtt.log'
    'Grid Schematics'  = 'D:\Projects\Space Engineers Stuff\Grid Schematics 2\output\probe.log'
}

foreach ($name in $logs.Keys) {
    $path = $logs[$name]
    Write-Host ""
    Write-Host "-- $name" -ForegroundColor White
    if (-not (Test-Path $path)) {
        Write-Host "   no log at $path" -ForegroundColor Yellow
        continue
    }
    $age = [int]((Get-Date) - (Get-Item $path).LastWriteTime).TotalSeconds
    $colour = if ($age -lt 30) { 'Green' } else { 'Yellow' }
    Write-Host "   last written ${age}s ago" -ForegroundColor $colour
    Get-Content $path -Tail 12 | ForEach-Object { Write-Host "   $_" }
}

Write-Host ""
Write-Host "=== RTT recon artefacts ===" -ForegroundColor Cyan
$recon = 'D:\Projects\Space Engineers Stuff\RTT Camera\output\scene-draw-recon.txt'
if (Test-Path $recon) {
    $sz = (Get-Item $recon).Length
    Write-Host "  scene-draw-recon.txt present ($sz bytes)" -ForegroundColor Green
} else {
    Write-Host "  scene-draw-recon.txt not written yet" -ForegroundColor Yellow
}
$marker = 'D:\Projects\Space Engineers Stuff\RTT Camera\output\blit-armed.marker'
if (Test-Path $marker) {
    Write-Host "  blit-armed.marker PRESENT - a previous run died with the blit armed" -ForegroundColor Red
}
