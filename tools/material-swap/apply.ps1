# Point TriplanarGIGlobal at the REAL triplanar shaders.
#
# WHY. The feed's terrain draws through the Indirect pass group, and the only terrain
# material declaring PassIndirect is TriplanarGIGlobal â€” "Low quality master terrain
# material used for GI. Uses Far3 colors." Its pixel shader is 52 lines and samples NO
# TEXTURES AT ALL:
#
#     cm += material.GetColorFar3().Values * matWeights[t];
#
# One flat colour per material. That is why asteroids are untextured in the feed, and it
# cannot be fixed by lighting, LOD, resolution or exposure.
#
# WHICH FILE. THIS MATTERS AND COST A TEST RUN. There are two trees:
#
#     Assets\Materials\States\    SOURCE for Keen's content pipeline. NOT loaded at
#                                 runtime. Editing it does nothing. It carries a .def.meta
#                                 with an MD5, which is what gave the misleading
#                                 impression that it was the authoritative copy.
#     Content\Materials\States\   BUILT OUTPUT. This is what the game loads. No .meta,
#                                 no hash, nothing to keep in sync.
#
# The giveaway was in the .meta all along: an AssetProcessingMetaDataItem with
# ProcessorInfo "P_DEF" and a ContentFiles.MainOutput entry. Assets is the input, Content
# is the output, and the output is what ships.
#
# Note Materials\Global\TriplanarGIGlobal.def is a different thing again â€” a
# MaterialDefinition whose DefaultState points at this state's GUID. Leave it alone.
#
# WHY THE SWAP SHOULD WORK. TriplanarGIGlobal and TriplanarSingleGlobal are identical
# everywhere that matters:
#
#     EntityDataType     TriplanarCustomData          same
#     InstanceDataType   InstanceCustomData           same
#     GlobalDataType     TriplanarGlobalCustomData    same
#     MaterialDataType   MaterialDefinition           same
#     VertexStream 0/1/2                              same
#     Topology / Blend / OrderLayer                   same
#
# Only the shaders, one macro and the pass flags differ. And TriplanarSinglePixel's guard
#
#     #if !defined(PASS_DEPTH) && !defined(PASS_DEPTH_CASCADE) &&
#         !defined(PASS_DEPTH_HOLOGRAM) &&
#         (!defined(PASS_DEFERRED_TEXTURING) || defined(PASS_DEFERRED_TEXTURING_MATERIAL))
#
# means that in an Indirect pass it takes the #else branch and samples triplanar textures
# DIRECTLY â€” no deferred-texturing stage, which is the stage we could not get working.
#
# Both shaders move together. The pair shares a Custom struct via
# TriplanarSingleShared.hlsli (scalar MatIndex, PositionObjectSpace,
# ObjectToViewSpaceQuaternion); the GI pair's is a different shape (MatWeights[3],
# MatIndex[3]). Mixing them will not compile.
#
# SCOPE. This edits the game install, not a mod, and TriplanarGIGlobal is used by the
# engine's own GI and reflection probes as well as our feed â€” so every probe face now
# runs a 172-line textured shader instead of a 52-line flat one. Expect a global cost;
# that is part of what this measures. restore.ps1 undoes it, as does Steam's file
# verification.

$ErrorActionPreference = 'Stop'

$def    = "E:\SteamLibrary\steamapps\common\SpaceEngineers2\VRage\GameData\Engine\Content\Materials\States\TriplanarGIGlobal.def"
$backup = Join-Path $PSScriptRoot "original\Content-TriplanarGIGlobal.def"

if (-not (Test-Path $backup)) {
    throw "No backup at $backup - refusing to modify the game install without one."
}
if (-not (Test-Path $def)) {
    throw "Not found: $def"
}

# Always start from the pristine backup, so re-running is idempotent rather than
# compounding edits.
$text = Get-Content $backup -Raw
$text = $text.Replace('geometry\\materials\\triplanargivertex.hlsl',
                      'geometry\\materials\\triplanarsinglevertex.hlsl')
$text = $text.Replace('geometry\\materials\\triplanargipixel.hlsl',
                      'geometry\\materials\\triplanarsinglepixel.hlsl')
Set-Content -Path $def -Value $text -NoNewline -Encoding utf8

Write-Output "Content\Materials\States\TriplanarGIGlobal.def"
Write-Output "  vertex -> triplanarsinglevertex.hlsl"
Write-Output "  pixel  -> triplanarsinglepixel.hlsl"
Get-Content $def | Select-String -Pattern 'FullPath|"Flags"' | ForEach-Object { "  " + $_.Line.Trim() }
Write-Output "Restart the game. restore.ps1 undoes it."
