<#
.SYNOPSIS
  Installs the sofabird Lua pages into the VLC user folder (level 1).

.DESCRIPTION
  VLC 3.0.x takes HTTP pages from the FIRST existing folder: %APPDATA%\vlc\lua\http,
  then <Program Files>\VideoLAN\VLC\lua\http. So the user folder must also contain
  the stock pages. The script copies the whole stock folder and adds
  requests\tracks.json and requests\setvar.json. Does not require admin rights.
  If VLC is running, suggests closing it (the pages are read when the interface starts).

.PARAMETER Uninstall
  Removes %APPDATA%\vlc\lua\http (returns VLC to the stock pages).
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$userHttp = Join-Path $env:APPDATA 'vlc\lua\http'

if ($Uninstall) {
  if (Test-Path $userHttp) { Remove-Item -Recurse -Force $userHttp; Write-Host "Removed: $userHttp" }
  else { Write-Host "Nothing to remove." }
  exit 0
}

$vlcDir = $null
try { $vlcDir = (Get-ItemProperty 'HKLM:\SOFTWARE\VideoLAN\VLC' -ErrorAction Stop).InstallDir } catch {}
if (-not $vlcDir) {
  foreach ($c in @("$env:ProgramFiles\VideoLAN\VLC", "${env:ProgramFiles(x86)}\VideoLAN\VLC")) {
    if (Test-Path (Join-Path $c 'vlc.exe')) { $vlcDir = $c; break }
  }
}
if (-not $vlcDir) { throw "VLC not found. Install VLC 3.x." }
$stockHttp = Join-Path $vlcDir 'lua\http'
if (-not (Test-Path $stockHttp)) { throw "Folder not found: $stockHttp" }

$version = (Get-Item (Join-Path $vlcDir 'vlc.exe')).VersionInfo.ProductVersion
Write-Host "VLC $version in $vlcDir"

New-Item -ItemType Directory -Force -Path $userHttp | Out-Null
Copy-Item -Path (Join-Path $stockHttp '*') -Destination $userHttp -Recurse -Force
$src = Join-Path $PSScriptRoot 'requests'
Copy-Item -Path (Join-Path $src 'tracks.json'), (Join-Path $src 'setvar.json') -Destination (Join-Path $userHttp 'requests') -Force
Set-Content -Path (Join-Path $userHttp 'sofabird.version') -Value "vlc=$version`ninstalled=$(Get-Date -Format o)" -Encoding ascii
Write-Host "Pages installed in $userHttp"

$running = Get-Process vlc -ErrorAction SilentlyContinue
if ($running) {
  Write-Warning "VLC is running. The new pages will work after it is restarted."
}
Write-Host "Check: http://localhost:8080/requests/tracks.json (VLC web interface password)."
