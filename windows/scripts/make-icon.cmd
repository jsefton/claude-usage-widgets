@echo off
rem Runs make-icon.ps1 without needing to change PowerShell's execution policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-icon.ps1" %*
exit /b %ERRORLEVEL%
