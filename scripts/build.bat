@echo off
rem Build both assemblies and deploy them to DeployDir (see Directory.Build.props).
rem The logic dll hot-reloads into a running game within ~2s; the bootstrap
rem requires a game restart.
cd /d "%~dp0.."
dotnet build src\RttProbe\RttProbe.csproj -c Release
dotnet build src\RttProbe.Logic\RttProbe.Logic.csproj -c Release
pause
