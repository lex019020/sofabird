<#
.SYNOPSIS
  Устанавливает Lua-страницы vlc-remote в пользовательскую папку VLC (уровень 1).

.DESCRIPTION
  VLC 3.0.x берёт HTTP-страницы из ПЕРВОЙ существующей папки: %APPDATA%\vlc\lua\http,
  затем <Program Files>\VideoLAN\VLC\lua\http. Поэтому пользовательская папка должна
  содержать и штатные страницы. Скрипт копирует штатную папку целиком и добавляет
  requests\tracks.json и requests\setvar.json. Не требует прав администратора.
  Если VLC запущен, предлагает закрыть его (страницы читаются при старте интерфейса).

.PARAMETER Uninstall
  Удаляет %APPDATA%\vlc\lua\http (возвращает VLC к штатным страницам).
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$userHttp = Join-Path $env:APPDATA 'vlc\lua\http'

if ($Uninstall) {
  if (Test-Path $userHttp) { Remove-Item -Recurse -Force $userHttp; Write-Host "Удалено: $userHttp" }
  else { Write-Host "Нечего удалять." }
  exit 0
}

$vlcDir = $null
try { $vlcDir = (Get-ItemProperty 'HKLM:\SOFTWARE\VideoLAN\VLC' -ErrorAction Stop).InstallDir } catch {}
if (-not $vlcDir) {
  foreach ($c in @("$env:ProgramFiles\VideoLAN\VLC", "${env:ProgramFiles(x86)}\VideoLAN\VLC")) {
    if (Test-Path (Join-Path $c 'vlc.exe')) { $vlcDir = $c; break }
  }
}
if (-not $vlcDir) { throw "VLC не найден. Установите VLC 3.x." }
$stockHttp = Join-Path $vlcDir 'lua\http'
if (-not (Test-Path $stockHttp)) { throw "Не найдена папка $stockHttp" }

$version = (Get-Item (Join-Path $vlcDir 'vlc.exe')).VersionInfo.ProductVersion
Write-Host "VLC $version в $vlcDir"

New-Item -ItemType Directory -Force -Path $userHttp | Out-Null
Copy-Item -Path (Join-Path $stockHttp '*') -Destination $userHttp -Recurse -Force
$src = Join-Path $PSScriptRoot 'requests'
Copy-Item -Path (Join-Path $src 'tracks.json'), (Join-Path $src 'setvar.json') -Destination (Join-Path $userHttp 'requests') -Force
Set-Content -Path (Join-Path $userHttp 'vlcremote.version') -Value "vlc=$version`ninstalled=$(Get-Date -Format o)" -Encoding ascii
Write-Host "Страницы установлены в $userHttp"

$running = Get-Process vlc -ErrorAction SilentlyContinue
if ($running) {
  Write-Warning "VLC запущен. Новые страницы заработают после его перезапуска."
}
Write-Host "Проверка: http://localhost:8080/requests/tracks.json (пароль веб-интерфейса VLC)."
