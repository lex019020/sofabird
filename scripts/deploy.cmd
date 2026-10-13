@echo off
rem Двойной клик: установить/обновить компаньон VLC Remote до последней тестовой сборки.
rem Параметры передаются в deploy.ps1, например: deploy.cmd -Apk
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
if errorlevel 1 pause
