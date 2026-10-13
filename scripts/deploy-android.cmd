@echo off
rem Double-click: VLC Remote deploy (android). Arguments are passed to deploy-android.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-android.ps1" %*
if errorlevel 1 pause
