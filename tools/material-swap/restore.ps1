# Put the shipped TriplanarGIGlobal back.
#
# Run this before any conclusion that depends on stock behaviour, and before reporting
# a bug to Keen. Steam's "Verify integrity of game files" does the same job if this
# script is ever lost.

$ErrorActionPreference = 'Stop'

$states = "D:\SteamLibrary\steamapps\common\SpaceEngineers2\VRage\GameData\Engine\Assets\Materials\States"
$backup = Join-Path $PSScriptRoot "original"

foreach ($f in @("TriplanarGIGlobal.def", "TriplanarGIGlobal.def.meta")) {
    $src = Join-Path $backup $f
    if (-not (Test-Path $src)) { throw "Missing backup: $src" }
    Copy-Item $src (Join-Path $states $f) -Force
    Write-Output "restored $f"
}

Write-Output "Restart the game for it to take effect."
