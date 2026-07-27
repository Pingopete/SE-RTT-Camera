# Put the shipped TriplanarGIGlobal back.
#
# Only Content\Materials\States is touched by apply.ps1 — that is the built output the
# game actually loads. Assets\Materials\States is the content-pipeline SOURCE and is not
# read at runtime; the copies of it here exist because it was edited first, by mistake,
# and are kept so that mistake stays documented.
#
# Steam's "Verify integrity of game files" does the same job if this script is lost.

$ErrorActionPreference = 'Stop'

$engine = "D:\SteamLibrary\steamapps\common\SpaceEngineers2\VRage\GameData\Engine"
$backup = Join-Path $PSScriptRoot "original"

Copy-Item (Join-Path $backup "Content-TriplanarGIGlobal.def") `
          (Join-Path $engine "Content\Materials\States\TriplanarGIGlobal.def") -Force
Write-Output "restored Content\Materials\States\TriplanarGIGlobal.def"

# Belt and braces: put the source tree back too, in case it was ever edited.
foreach ($f in @("TriplanarGIGlobal.def", "TriplanarGIGlobal.def.meta")) {
    Copy-Item (Join-Path $backup $f) (Join-Path $engine "Assets\Materials\States\$f") -Force
}
Write-Output "restored Assets\Materials\States (source tree, not loaded at runtime)"
Write-Output "Restart the game for it to take effect."
