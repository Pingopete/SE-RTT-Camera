@echo off
rem Launch SE2 with the bootstrap plugin via Keen's own -plugins: argument.
rem Steam must be running. Build first: scripts\build.bat
rem
rem PluginHost.LoadPlugins splits the argument on ';', so several plugins can be
rem loaded at once. Set BOTH=1 to also load the Grid Schematics bootstrap.
rem Deploy-directory copies, not bin\Release: each has 0Harmony.dll beside it,
rem and rebuilding never fights a DLL the running game holds open.
setlocal
set RTT_DLL=D:\SE2Rtt\RttProbe.dll
set GS_DLL=D:\SE2Probe\GridProbe.dll

if not exist "%RTT_DLL%" (
  echo Plugin DLL not found: %RTT_DLL%
  echo Run scripts\build.bat first.
  pause
  exit /b 1
)

rem Both mods by default; set RTT_ONLY=1 for a clean single-mod run.
set PLUGIN_ARG=%RTT_DLL%
if not "%RTT_ONLY%"=="1" (
  if exist "%GS_DLL%" (
    set PLUGIN_ARG=%RTT_DLL%;%GS_DLL%
    echo Loading RttProbe + GridProbe.
  ) else (
    echo GridProbe.dll not found, loading RttProbe only.
  )
)

cd /d "E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2"
start "" SpaceEngineers2.exe "-plugins:%PLUGIN_ARG%"
echo Launched. Watch output\rtt.log, then output\scene-draw-recon.txt.
endlocal
rem If the log never appears, Steam may have relaunched the exe without args:
rem put the same -plugins:... string in Steam -> SE2 -> Properties -> Launch
rem Options and start from Steam instead.
