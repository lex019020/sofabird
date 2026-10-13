<#
.SYNOPSIS
  Установка и обновление приложения VLC Remote на телефон по adb.

.DESCRIPTION
  Источник: GitHub Releases (по умолчанию; нужен gh с доступом к репозиторию) или -Local — сборка из рабочей
  копии (нужен android\keystore.properties). Канал beta — самая свежая сборка, stable — последний релиз.

  APK ставится ТОЛЬКО на устройства из списка разрешённых (%USERPROFILE%\.vlc-remote\devices.json, по
  серийному номеру ro.serialno). Остальные подключённые по adb устройства — например, велокомпьютер —
  пропускаются. Пустой список = отказ.

  Подключение по Wi‑Fi (Android 11+): «Для разработчиков» → «Отладка по Wi‑Fi». Один раз сопрячь:
  adb pair <ip>:<порт сопряжения> <код>; затем при каждой смене сети/перезагрузке: -Connect <ip>:<порт>.

.EXAMPLE
  .\deploy-android.ps1                              # последняя тестовая сборка на разрешённые телефоны
  .\deploy-android.ps1 -Connect 192.168.0.16:36457  # явно указать адрес (обычно не нужно: телефон ищется сам)
  .\deploy-android.ps1 -Channel stable              # последний релиз
  .\deploy-android.ps1 -Local                       # собрать из исходников и поставить
  .\deploy-android.ps1 -CheckOnly                   # какие версии стоят на телефонах
  .\deploy-android.ps1 -ListDevices                 # подключённые и разрешённые устройства
  .\deploy-android.ps1 -AllowDevice RFCWC0SSKZK     # разрешить устройство (серийный номер)
  .\deploy-android.ps1 -DisallowDevice RFCWC0SSKZK  # запретить
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
    [string]$DevicesFile = (Join-Path $env:USERPROFILE '.vlc-remote\devices.json'),
    [string]$Repo = 'lex019020/vlc-remote'
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$PackageId = 'dev.vlcremote.app'
$ApkKeepDir = Join-Path $VlcrDataRoot 'apk'

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
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    # Устройства в состоянии "device" с настоящим серийным номером (у Wi‑Fi‑отладки id = ip:порт).
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
    # Порт отладки по Wi-Fi случайный (на практике 30000–49999); быстрый параллельный TCP-скан одного адреса.
    $open = New-Object System.Collections.Generic.List[int]
    for ($start = $From; $start -le $To; $start += $Batch) {
        $end = [Math]::Min($To, $start + $Batch - 1)
        $pending = @()
        for ($port = $start; $port -le $end; $port++) {
            $c = New-Object System.Net.Sockets.TcpClient
            $pending += [pscustomobject]@{ Port = $port; Client = $c; Task = $c.ConnectAsync($Ip, $port) }
        }
        try { [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]($pending | ForEach-Object { $_.Task }), $TimeoutMs) } catch {}
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
    # Освежить ARP: пинг всей /24 своей сети (параллельно), затем ещё раз посмотреть таблицу.
    $own = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixLength -eq 24 -and $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' -and $_.InterfaceAlias -notmatch 'vEthernet|Loopback|Local Area Connection\*' }
    foreach ($o in $own) {
        $prefix = $o.IPAddress.Substring(0, $o.IPAddress.LastIndexOf('.') + 1)
        $pings = 1..254 | ForEach-Object { (New-Object System.Net.NetworkInformation.Ping).SendPingAsync("$prefix$_", 600) }
        try { [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$pings, 2500) } catch {}
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
    # Найти и подключить разрешённые телефоны без ручного ввода порта:
    # 1) уже подключён; 2) последний удачный адрес; 3) mDNS adb; 4) IP по MAC (ARP) или прежний IP + скан портов.
    $ErrorActionPreference = 'Continue'
    $adb = Find-Adb
    $allowed = @(Get-AllowedDevices)
    $connected = @(Get-ConnectedDevices)
    foreach ($dev in $allowed) {
        if ($connected | Where-Object { $_.Serial -eq $dev.serial }) { continue }
        $name = if ($dev.name) { $dev.name } else { $dev.serial }
        Write-Step "Ищу телефон $name"
        $done = $null
        if ($dev.lastIp -and $dev.lastPort -and (Test-AdbConnect "$($dev.lastIp):$($dev.lastPort)")) { $done = "$($dev.lastIp):$($dev.lastPort)" }
        if (-not $done) {
            $svc = & $adb mdns services 2>$null | Where-Object { $_ -match [regex]::Escape($dev.serial) -and $_ -match '_adb-tls-connect' } | Select-Object -First 1
            if ($svc -and $svc -match '(\d{1,3}(\.\d{1,3}){3}:\d+)') {
                if (Test-AdbConnect $Matches[1]) { $done = $Matches[1] }
            }
        }
        if (-not $done) {
            $ip = Find-IpByMac $dev.mac
            if (-not $ip) { $ip = $dev.lastIp }
            if ($ip) {
                Write-Note "Проверяю порты отладки на $ip…"
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
            Write-Ok "Подключено: $done"
        } else {
            Write-Note "Телефон $name не найден в сети. Проверьте, что он не спит, в той же сети и «Отладка по Wi‑Fi» включена."
        }
    }
}

function Get-InstalledApkVersion([string]$Id) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    $adb = Find-Adb
    $line = & $adb -s $Id shell dumpsys package $PackageId 2>$null | Where-Object { $_ -match 'versionName=' } | Select-Object -First 1
    if ($line -match 'versionName=(\S+)') { return $Matches[1] }
    return $null
}

function Get-TargetDevices {
    $allowed = @(Get-AllowedDevices)
    if ($allowed.Count -eq 0) {
        throw "Список разрешённых устройств пуст ($DevicesFile). Подключите телефон, посмотрите серийный номер (-ListDevices) и добавьте: -AllowDevice <серийный номер>."
    }
    $targets = @()
    foreach ($d in @(Get-ConnectedDevices)) {
        if ($allowed | Where-Object { $_.serial -eq $d.Serial }) { $targets += $d }
        else { Write-Note "Пропущено: $($d.Model) ($($d.Serial)) — нет в списке разрешённых" }
    }
    if ($targets.Count -eq 0) {
        Write-Note "Разрешённый телефон не подключён ($(($allowed | ForEach-Object { $_.name }) -join ', ')). Wi‑Fi‑отладка: -Connect <ip>:<порт>."
    }
    return $targets  # callers wrap in @(): empty -> @(), single object -> 1-element array
}

function Show-Devices {
    $allowed = @(Get-AllowedDevices)
    Write-Step "Разрешённые устройства ($DevicesFile)"
    if ($allowed.Count -eq 0) { Write-Note 'список пуст' }
    foreach ($a in $allowed) { Write-Host "    $($a.serial)  $($a.name)" }
    Write-Step 'Подключённые по adb'
    $connected = @(Get-ConnectedDevices)
    if ($connected.Count -eq 0) { Write-Note 'нет' }
    foreach ($d in $connected) {
        $ok = [bool]($allowed | Where-Object { $_.serial -eq $d.Serial })
        $mark = if ($ok) { '[разрешено]' } else { '[пропускается]' }
        Write-Host "    $($d.Serial)  $($d.Model)  ($($d.Id))  $mark"
    }
}

function Install-ApkTo([object]$Device, [string]$ApkPath) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    $adb = Find-Adb
    $out = & $adb -s $Device.Id install -r $ApkPath 2>&1 | Out-String
    if ($out -match 'Success') { Write-Ok "Установлено: $($Device.Model) ($($Device.Serial))"; return }
    if ($out -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        $ErrorActionPreference = 'Stop'
        throw "$($Device.Model): стоит сборка с другой подписью. Удалите её (adb -s $($Device.Id) uninstall $PackageId — сопряжения и настройки пропадут) и повторите."
    }
    if ($out -match 'INSTALL_FAILED_VERSION_DOWNGRADE') {
        $ErrorActionPreference = 'Stop'
        throw "$($Device.Model): на телефоне более новая сборка ($(if ($out -match 'Downgrade detected: ([^\]]+)') { $Matches[1].Trim() } else { 'versionCode' })). Поставьте сборку новее или удалите приложение (adb -s $($Device.Id) uninstall $PackageId — сопряжения и настройки пропадут)."
    }
    $ErrorActionPreference = 'Stop'
    throw "adb install ($($Device.Model)): $($out.Trim())"
}

# ---------------------------------------------------------------- основное

if ($Connect) {
    $adb = Find-Adb
    Write-Step "adb connect $Connect"
    $ErrorActionPreference = 'Continue'
    $out = & $adb connect $Connect 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($out -notmatch 'connected to') { throw "Не удалось подключиться: $($out.Trim()). Проверьте адрес и порт на экране «Отладка по Wi‑Fi»; при первом подключении сначала adb pair." }
    Write-Ok $out.Trim()
    $serial = (& $adb -s $Connect shell getprop ro.serialno 2>$null | Out-String).Trim()
    if ($serial -and $Connect -match '^(\d{1,3}(?:\.\d{1,3}){3}):(\d+)$') { Update-AllowedDevice $serial @{ lastIp = $Matches[1]; lastPort = [int]$Matches[2] } }
}

if (-not $NoAutoConnect -and -not ($AllowDevice -or $DisallowDevice)) { Connect-AllowedDevices }

if ($AllowDevice -or $DisallowDevice) {
    $list = @(Get-AllowedDevices)
    if ($DisallowDevice) {
        $list = @($list | Where-Object { $_.serial -ne $DisallowDevice })
        Write-Ok "Удалено из разрешённых: $DisallowDevice"
    }
    if ($AllowDevice) {
        $name = $AllowDevice
        $hit = Get-ConnectedDevices | Where-Object { $_.Serial -eq $AllowDevice } | Select-Object -First 1
        if ($hit) { $name = $hit.Model } else { Write-Note "Устройство $AllowDevice сейчас не подключено — добавляю без проверки" }
        $list = @($list | Where-Object { $_.serial -ne $AllowDevice }) + @([pscustomobject]@{ serial = $AllowDevice; name = $name })
        Write-Ok "Разрешено: $AllowDevice ($name)"
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
        $build = Build-LocalApk $work
        $target = $build.Version
    } else {
        Assert-Gh
        $tagName = Resolve-ReleaseTag
        $target = $tagName.TrimStart('v')
        $targets = @(Get-TargetDevices)
    }
    Write-Step "Приложение: $target"
    $pending = @()
    foreach ($t in $targets) {
        $current = Get-InstalledApkVersion $t.Id
        Write-Host "    $($t.Model) ($($t.Serial)): установлено $(if ($current) { $current } else { 'нет' })"
        if ($Force -or $current -ne $target) { $pending += $t }
    }
    if ($CheckOnly) {
        if ($pending.Count -gt 0) { Write-Note 'Есть обновление' } elseif ($targets.Count -gt 0) { Write-Ok 'Установлена последняя версия' }
        return
    }
    if ($targets.Count -gt 0 -and $pending.Count -eq 0) { Write-Ok 'Уже установлена эта версия (-Force — переустановить)'; return }

    $apk = if ($Local) { $build.File } else { Get-ReleaseAsset $tagName 'VlcRemote-*.apk' $work }
    if ($pending.Count -eq 0) {
        if (-not (Test-Path $ApkKeepDir)) { New-Item -ItemType Directory -Path $ApkKeepDir -Force | Out-Null }
        Copy-Item $apk $ApkKeepDir -Force
        Write-Note "APK сохранён: $(Join-Path $ApkKeepDir (Split-Path $apk -Leaf)) — его можно и просто передать на телефон."
        return
    }
    Write-Step "Установка $(Split-Path $apk -Leaf)"
    foreach ($t in $pending) { Install-ApkTo $t $apk }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
