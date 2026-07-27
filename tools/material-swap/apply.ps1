# Point TriplanarGIGlobal at the REAL triplanar shaders.
#
# WHY. The feed's terrain is drawn through the Indirect pass group, and the only
# terrain material that declares PassIndirect is TriplanarGIGlobal — which describes
# itself as "Low quality master terrain material used for GI. Uses Far3 colors." Its
# pixel shader is 52 lines and samples NO TEXTURES AT ALL:
#
#     cm += material.GetColorFar3().Values * matWeights[t];
#
# One flat colour per material. That is why asteroids are untextured in the feed, and
# it is not fixable by lighting, LOD, resolution or exposure.
#
# TriplanarSingleGlobal has the real 172-line shader, but declares PassGBuffer /
# PassDepth / DeferredTexturing and NOT PassIndirect — so it never draws in our pass.
# Getting the main-view pass groups working is the long route; this is the short one.
#
# WHY IT SHOULD WORK. The two material states are identical everywhere that matters:
#
#     EntityDataType     TriplanarCustomData          same
#     InstanceDataType   InstanceCustomData           same
#     GlobalDataType     TriplanarGlobalCustomData    same
#     MaterialDataType   MaterialDefinition           same
#     VertexStream 0/1/2                              same
#     Topology / Blend / OrderLayer                   same
#
# Only the shaders, one macro and the pass flags differ. And TriplanarSinglePixel's
# guard means that with no PASS_DEPTH* and no PASS_DEFERRED_TEXTURING defined — which
# is the Indirect pass — it takes the #else branch and samples triplanar textures
# directly, with no deferred-texturing stage required.
#
# The vertex shader must move WITH the pixel shader: the two files share a Custom
# struct via TriplanarSingleShared.hlsli (scalar MatIndex, PositionObjectSpace,
# ObjectToViewSpaceQuaternion) which is a different shape from the GI pair's
# (MatWeights[3], MatIndex[3]). Mixing them would not compile.
#
# SCOPE OF THE CHANGE. This is a game-install edit, not a mod, and TriplanarGIGlobal
# is used by the engine's OWN GI and reflection probes as well as our feed. Expect a
# global cost: every probe face now runs a 172-line textured shader instead of a
# 52-line flat one. That is the trade being tested — restore.ps1 puts it back.
#
# Steam's "Verify integrity of game files" also restores the original.

$ErrorActionPreference = 'Stop'

$states  = "D:\SteamLibrary\steamapps\common\SpaceEngineers2\VRage\GameData\Engine\Assets\Materials\States"
$def     = Join-Path $states "TriplanarGIGlobal.def"
$meta    = Join-Path $states "TriplanarGIGlobal.def.meta"
$backup  = Join-Path $PSScriptRoot "original"

if (-not (Test-Path (Join-Path $backup "TriplanarGIGlobal.def"))) {
    throw "No backup in $backup - refusing to modify the game install without one."
}

# Swap both shaders. They are a matched pair; see the note above.
$text = Get-Content $def -Raw
$text = $text.Replace('geometry\\materials\\triplanargivertex.hlsl',
                      'geometry\\materials\\triplanarsinglevertex.hlsl')
$text = $text.Replace('geometry\\materials\\triplanargipixel.hlsl',
                      'geometry\\materials\\triplanarsinglepixel.hlsl')
Set-Content -Path $def -Value $text -NoNewline -Encoding utf8

# The .meta carries an MD5 of the .def, base64-encoded, in three places. Verified by
# reproducing the shipped value exactly. Whether the loader checks it is unknown, but
# leaving it stale is a needless way to fail.
$hash = [Convert]::ToBase64String(
    [System.Security.Cryptography.MD5]::Create().ComputeHash(
        [System.IO.File]::ReadAllBytes($def)))

$old = (Get-Content (Join-Path $backup "TriplanarGIGlobal.def.meta") -Raw |
        Select-String -Pattern '"FileHash": "([^"]+)"' -AllMatches).Matches[0].Groups[1].Value

$metaText = Get-Content (Join-Path $backup "TriplanarGIGlobal.def.meta") -Raw
$metaText = $metaText.Replace($old, $hash)
Set-Content -Path $meta -Value $metaText -NoNewline -Encoding utf8

Write-Output "TriplanarGIGlobal -> triplanarsingle{vertex,pixel}.hlsl"
Write-Output "  md5 $old -> $hash"
Write-Output "Restart the game. Run restore.ps1 to undo."
