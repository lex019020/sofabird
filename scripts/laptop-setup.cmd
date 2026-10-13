@echo off
rem Run on the TV laptop: one-time OpenSSH setup for remote debugging (asks for admin rights).
rem The current (non-elevated) user name is passed on, so the key goes to the right account.
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '-UserName %USERNAME% %*' -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0laptop-setup.ps1" %*
pause
