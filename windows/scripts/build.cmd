@echo off
rem Runs build.ps1 without needing to change PowerShell's execution policy
rem (Windows blocks .ps1 scripts by default). Arguments are passed through, e.g.:
rem   scripts\build.cmd -Install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
