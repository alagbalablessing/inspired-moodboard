@echo off
rem Builds "Inspired Moodboard V.1 Setup.exe" into the dist folder. Double-click to run.
rem   build.cmd          build the installer
rem   build.cmd run      also open the freshly built app straight away (no installing)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
