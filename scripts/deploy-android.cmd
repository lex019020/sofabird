@echo off
rem Double-click: Sofabird deploy (android). Arguments are passed to deploy-android.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-android.ps1" %*
if errorlevel 1 pause
