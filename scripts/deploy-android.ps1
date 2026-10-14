<#
.SYNOPSIS
  Installs and updates the Sofabird app on a phone over adb.

.DESCRIPTION
  Source: GitHub Releases (default; no login needed) or -Local - a build from the working
  copy (requires android\keystore.properties). The beta channel is the newest build, stable is the latest release.

  The APK is installed ONLY on devices from the allow list (%USERPROFILE%\.sofabird\devices.json, by the
  ro.serialno serial number). Other devices connected over adb - for example, a bike computer - are skipped.
  An empty list = refusal.

  Wi-Fi connection (Android 11+): "Developer options" -> "Wireless debugging". Pair once:
  adb pair <ip>:<pairing port> <code>; then after every network change/reboot: -Connect <ip>:<port>.

.EXAMPLE
  .\deploy-android.ps1                              # latest test build to the allowed phones
  .\deploy-android.ps1 -Connect 192.168.1.50:37123  # give the address explicitly (usually not needed: the phone is found automatically)
  .\deploy-android.ps1 -Channel stable              # latest release
  .\deploy-android.ps1 -Local                       # build from source and install
  .\deploy-android.ps1 -CheckOnly                   # which versions are installed on the phones
  .\deploy-android.ps1 -ListDevices                 # connected and allowed devices
  .\deploy-android.ps1 -AllowDevice R5CX1234567     # allow a device (serial number)
  .\deploy-android.ps1 -DisallowDevice R5CX1234567  # disallow it
#>
[CmdletBinding()]
param(
    [ValidateSet('beta', 'stable')]
    [string]$Channel = 'beta',
    [string]$Tag,
    [switch]$Local,
    [string]$Connect,
    [switch]$CheckOnly,
    [switch]$Force,
    [switch]$ListDevices,
    [switch]$NoAutoConnect,
    [string]$AllowDevice,
    [string]$DisallowDevice,
    [string]$DevicesFile = (Join-Path $env:USERPROFILE '.sofabird\devices.json'),
    [string]$Repo = 'lex019020/sofabird'
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$PackageId = 'app.sofabird'
$LegacyPackageId = 'dev.vlcremote.app'   # the app under its earlier name, VLC Remote
$ApkKeepDir = Join-Path $SofabirdDataRoot 'apk'

# The device list lived in %USERPROFILE%\.vlc-remote before the rename: carried over once.
$legacyDevices = Join-Path $env:USERPROFILE '.vlc-remote\devices.json'
if (-not $PSBoundParameters.ContainsKey('DevicesFile') -and -not (Test-Path $DevicesFile) -and (Test-Path $legacyDevices)) {
    New-Item -ItemType Directory -Force (Split-Path $DevicesFile) | Out-Null
    Copy-Item $legacyDevices $DevicesFile
}

function Get-AllowedDevices {
    if (-not (Test-Path $DevicesFile)) { return @() }
    $cfg = Get-Content $DevicesFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $cfg.apkDevices) { return @() }
    return @($cfg.apkDevices)
}

function Save-AllowedDevices([object[]]$Devices) {
    $dir = Split-Path $DevicesFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $json = ConvertTo-Json -InputObject @{ apkDevices = @($Devices) } -Depth 4
    [IO.File]::WriteAllText($DevicesFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function Get-ConnectedDevices {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    # Devices in the "device" state with their real serial number (for Wi-Fi debugging the id is ip:port).
    $adb = Find-Adb
    $result = @()
    $lines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' }
    foreach ($line in $lines) {
        $id = ($line -split "`t")[0]
        $props = & $adb -s $id shell 'getprop ro.serialno; getprop ro.product.manufacturer; getprop ro.product.model' 2>$null
        $props = @($props | ForEach-Object { "$_".Trim() })
        $result += [pscustomobject]@{
            Id     = $id
            Serial = $(if ($props.Count -gt 0) { $props[0] } else { '' })
            Model  = (($props | Select-Object -Skip 1) -join ' ').Trim()
        }
    }
    return $result
}

function Update-AllowedDevice([string]$Serial, [hashtable]$Values) {
    $list = @(Get-AllowedDevices)
    foreach ($d in $list) {
        if ($d.serial -ne $Serial) { continue }
        foreach ($k in $Values.Keys) {
            if ($null -eq $Values[$k]) { continue }
            if ($d.PSObject.Properties[$k]) { $d.$k = $Values[$k] } else { $d | Add-Member -NotePropertyName $k -NotePropertyValue $Values[$k] }
        }
    }
    Save-AllowedDevices $list
}

function Get-OpenPorts([string]$Ip, [int]$From = 30000, [int]$To = 49999, [int]$TimeoutMs = 300, [int]$Batch = 1500) {
    # The Wi-Fi debugging port is random (in practice 30000-49999); a fast parallel TCP scan of a single address.
    $open = New-Object System.Collections.Generic.List[int]
    for ($start = $From; $start -le $To; $start += $Batch) {
        $end = [Math]::Min($To, $start + $Batch - 1)
        $pending = @()
        for ($port = $start; $port -le $end; $port++) {
            $c = New-Object System.Net.Sockets.TcpClient
            $pending += [pscustomobject]@{ Port = $port; Client = $c; Task = $c.ConnectAsync($Ip, $port) }
        }
        try { [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]($pending | ForEach-Object { $_.Task }), $TimeoutMs) }
        catch { Write-Verbose "Port scan: some connections failed ($($_.Exception.Message)); the open ports are read below" }
        foreach ($x in $pending) {
            if ($x.Task.Status -eq 'RanToCompletion' -and $x.Client.Connected) { $open.Add($x.Port) }
            $x.Client.Dispose()
        }
    }
    return $open.ToArray()
}

function Find-IpByMac([string]$Mac) {
    if (-not $Mac) { return $null }
    $norm = ($Mac -replace '[:\-]', '').ToUpperInvariant()
    $hit = Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { ($_.LinkLayerAddress -replace '[:\-]', '').ToUpperInvariant() -eq $norm -and $_.State -ne 'Unreachable' } | Select-Object -First 1
    if ($hit) { return $hit.IPAddress }
    # Refresh ARP: ping the whole /24 of our own network (in parallel), then look at the table again.
    $own = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixLength -eq 24 -and $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' -and $_.InterfaceAlias -notmatch 'vEthernet|Loopback|Local Area Connection\*' }
    foreach ($o in $own) {
        $prefix = $o.IPAddress.Substring(0, $o.IPAddress.LastIndexOf('.') + 1)
        $pings = 1..254 | ForEach-Object { (New-Object System.Net.NetworkInformation.Ping).SendPingAsync("$prefix$_", 600) }
        try { [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$pings, 2500) }
        catch { Write-Verbose "Ping sweep: some hosts did not answer ($($_.Exception.Message)); it only fills the ARP table" }
    }
    $hit = Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { ($_.LinkLayerAddress -replace '[:\-]', '').ToUpperInvariant() -eq $norm -and $_.State -ne 'Unreachable' } | Select-Object -First 1
    if ($hit) { return $hit.IPAddress }
    return $null
}

function Test-AdbConnect([string]$Address) {
    $ErrorActionPreference = 'Continue'
    $adb = Find-Adb
    $out = & $adb connect $Address 2>&1 | Out-String
    if ($out -notmatch 'connected to') { return $false }
    Start-Sleep -Milliseconds 600
    $state = (& $adb -s $Address get-state 2>&1 | Out-String).Trim()
    if ($state -eq 'device') { return $true }
    & $adb disconnect $Address 2>&1 | Out-Null
    return $false
}

function Connect-AllowedDevices {
    # Find and connect the allowed phones without typing the port by hand:
    # 1) already connected; 2) last successful address; 3) adb mDNS; 4) IP by MAC (ARP) or the previous IP + port scan.
    $ErrorActionPreference = 'Continue'
    $adb = Find-Adb
    $allowed = @(Get-AllowedDevices)
    $connected = @(Get-ConnectedDevices)
    foreach ($dev in $allowed) {
        if ($connected | Where-Object { $_.Serial -eq $dev.serial }) { continue }
        $name = if ($dev.name) { $dev.name } else { $dev.serial }
        Write-Step "Looking for phone $name"
        $done = $null
        if ($dev.lastIp -and $dev.lastPort -and (Test-AdbConnect "$($dev.lastIp):$($dev.lastPort)")) { $done = "$($dev.lastIp):$($dev.lastPort)" }
        if (-not $done) {
            # mDNS may also hold stale records (an old port): try all addresses, newest (last) first.
            $addrs = @(& $adb mdns services 2>$null | Where-Object { $_ -match [regex]::Escape($dev.serial) -and $_ -match '_adb-tls-connect' } |
                ForEach-Object { if ($_ -match '(\d{1,3}(?:\.\d{1,3}){3}:\d+)') { $Matches[1] } } | Select-Object -Unique)
            [array]::Reverse($addrs)
            foreach ($a in $addrs) { if (Test-AdbConnect $a) { $done = $a; break } }
        }
        if (-not $done) {
            $ip = Find-IpByMac $dev.mac
            if (-not $ip) { $ip = $dev.lastIp }
            if ($ip) {
                Write-Note "Checking debugging ports on $ip..."
                foreach ($port in (Get-OpenPorts $ip)) {
                    if (Test-AdbConnect "${ip}:$port") { $done = "${ip}:$port"; break }
                }
            }
        }
        if ($done) {
            $parts = $done.Split(':')
            $mac = $null
            $line = & $adb -s $done shell 'ip link show wlan0' 2>$null | Where-Object { $_ -match 'link/ether ([0-9a-f:]{17})' } | Select-Object -First 1
            if ($line -match 'link/ether ([0-9a-f:]{17})') { $mac = $Matches[1] }
            Update-AllowedDevice $dev.serial @{ lastIp = $parts[0]; lastPort = [int]$parts[1]; mac = $mac }
            Write-Ok "Connected: $done"
        } else {
            Write-Note "Phone $name was not found on the network. Make sure it is awake, on the same network and that ""Wireless debugging"" is on."
        }
    }
}

function Get-InstalledApkVersion([string]$Id) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    $adb = Find-Adb
    $line = & $adb -s $Id shell dumpsys package $PackageId 2>$null | Where-Object { $_ -match 'versionName=' } | Select-Object -First 1
    if ($line -match 'versionName=(\S+)') { return $Matches[1] }
    return $null
}

function Get-TargetDevices {
    $allowed = @(Get-AllowedDevices)
    if ($allowed.Count -eq 0) {
        throw "The list of allowed devices is empty ($DevicesFile). Connect the phone, look up its serial number (-ListDevices) and add it: -AllowDevice <serial number>."
    }
    $targets = @()
    foreach ($d in @(Get-ConnectedDevices)) {
        if ($allowed | Where-Object { $_.serial -eq $d.Serial }) { $targets += $d }
        else { Write-Note "Skipped: $($d.Model) ($($d.Serial)) - not in the allow list" }
    }
    if ($targets.Count -eq 0) {
        Write-Note "No allowed phone is connected ($(($allowed | ForEach-Object { $_.name }) -join ', ')). Wi-Fi debugging: -Connect <ip>:<port>."
    }
    return $targets  # callers wrap in @(): empty -> @(), single object -> 1-element array
}

function Show-Devices {
    $allowed = @(Get-AllowedDevices)
    Write-Step "Allowed devices ($DevicesFile)"
    if ($allowed.Count -eq 0) { Write-Note 'the list is empty' }
    foreach ($a in $allowed) { Write-Host "    $($a.serial)  $($a.name)" }
    Write-Step 'Connected over adb'
    $connected = @(Get-ConnectedDevices)
    if ($connected.Count -eq 0) { Write-Note 'none' }
    foreach ($d in $connected) {
        $ok = [bool]($allowed | Where-Object { $_.serial -eq $d.Serial })
        $mark = if ($ok) { '[allowed]' } else { '[skipped]' }
        Write-Host "    $($d.Serial)  $($d.Model)  ($($d.Id))  $mark"
    }
}

function Install-ApkTo([object]$Device, [string]$ApkPath) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    $adb = Find-Adb
    $out = & $adb -s $Device.Id install -r $ApkPath 2>&1 | Out-String
    if ($out -match 'Success') {
        Write-Ok "Installed: $($Device.Model) ($($Device.Serial))"
        if (& $adb -s $Device.Id shell pm list packages $LegacyPackageId 2>$null | Where-Object { $_.Trim() -eq "package:$LegacyPackageId" }) {
            Write-Note "$($Device.Model): the old VLC Remote app is still installed; remove it once Sofabird works (adb -s $($Device.Id) uninstall $LegacyPackageId)."
        }
        return
    }
    if ($out -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        $ErrorActionPreference = 'Stop'
        throw "$($Device.Model): a build with a different signature is installed. Uninstall it (adb -s $($Device.Id) uninstall $PackageId - pairings and settings will be lost) and try again."
    }
    if ($out -match 'INSTALL_FAILED_VERSION_DOWNGRADE') {
        $ErrorActionPreference = 'Stop'
        throw "$($Device.Model): the phone has a newer build ($(if ($out -match 'Downgrade detected: ([^\]]+)') { $Matches[1].Trim() } else { 'versionCode' })). Install a newer build or uninstall the app (adb -s $($Device.Id) uninstall $PackageId - pairings and settings will be lost)."
    }
    $ErrorActionPreference = 'Stop'
    throw "adb install ($($Device.Model)): $($out.Trim())"
}

# ---------------------------------------------------------------- main

if ($Connect) {
    $adb = Find-Adb
    Write-Step "adb connect $Connect"
    $ErrorActionPreference = 'Continue'
    $out = & $adb connect $Connect 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($out -notmatch 'connected to') { throw "Could not connect: $($out.Trim()). Check the address and port on the ""Wireless debugging"" screen; for the first connection run adb pair first." }
    Write-Ok $out.Trim()
    $serial = (& $adb -s $Connect shell getprop ro.serialno 2>$null | Out-String).Trim()
    if ($serial -and $Connect -match '^(\d{1,3}(?:\.\d{1,3}){3}):(\d+)$') { Update-AllowedDevice $serial @{ lastIp = $Matches[1]; lastPort = [int]$Matches[2] } }
}

if (-not $NoAutoConnect -and -not ($AllowDevice -or $DisallowDevice)) { Connect-AllowedDevices }

if ($AllowDevice -or $DisallowDevice) {
    $list = @(Get-AllowedDevices)
    if ($DisallowDevice) {
        $list = @($list | Where-Object { $_.serial -ne $DisallowDevice })
        Write-Ok "Removed from the allow list: $DisallowDevice"
    }
    if ($AllowDevice) {
        $name = $AllowDevice
        $hit = Get-ConnectedDevices | Where-Object { $_.Serial -eq $AllowDevice } | Select-Object -First 1
        if ($hit) { $name = $hit.Model } else { Write-Note "Device $AllowDevice is not connected right now - adding it without a check" }
        $list = @($list | Where-Object { $_.serial -ne $AllowDevice }) + @([pscustomobject]@{ serial = $AllowDevice; name = $name })
        Write-Ok "Allowed: $AllowDevice ($name)"
    }
    Save-AllowedDevices $list
    Show-Devices
    return
}

if ($ListDevices) { Show-Devices; return }

$work = New-WorkDir
try {
    if ($Local) {
        $targets = @(Get-TargetDevices)
        if ($targets.Count -eq 0 -and -not $CheckOnly) { return }
        $build = Invoke-LocalApkBuild $work
        $target = $build.Version
    } else {
        $tagName = Resolve-ReleaseTag -Repo $Repo -Channel $Channel -Tag $Tag
        $target = $tagName.TrimStart('v')
        $targets = @(Get-TargetDevices)
    }
    Write-Step "App: $target"
    $pending = @()
    foreach ($t in $targets) {
        $current = Get-InstalledApkVersion $t.Id
        Write-Host "    $($t.Model) ($($t.Serial)): installed $(if ($current) { $current } else { 'none' })"
        if ($Force -or $current -ne $target) { $pending += $t }
    }
    if ($CheckOnly) {
        if ($pending.Count -gt 0) { Write-Note 'An update is available' } elseif ($targets.Count -gt 0) { Write-Ok 'The latest version is installed' }
        return
    }
    if ($targets.Count -gt 0 -and $pending.Count -eq 0) { Write-Ok 'This version is already installed (-Force to reinstall)'; return }

    $apk = if ($Local) { $build.File } else { Get-ReleaseAsset $Repo $tagName 'Sofabird-*.apk' $work }
    if ($pending.Count -eq 0) {
        if (-not (Test-Path $ApkKeepDir)) { New-Item -ItemType Directory -Path $ApkKeepDir -Force | Out-Null }
        Copy-Item $apk $ApkKeepDir -Force
        Write-Note "APK saved: $(Join-Path $ApkKeepDir (Split-Path $apk -Leaf)) - you can also just copy it to the phone."
        return
    }
    Write-Step "Installing $(Split-Path $apk -Leaf)"
    foreach ($t in $pending) { Install-ApkTo $t $apk }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
