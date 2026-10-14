@echo off
rem Double-click: Sofabird deploy (windows). Arguments are passed to deploy-windows.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-windows.ps1" %*
if errorlevel 1 pause
