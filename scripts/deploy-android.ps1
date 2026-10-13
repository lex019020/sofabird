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
  .\deploy-android.ps1 -Connect 192.168.0.16:36457  # сначала adb connect, потом установка
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
    return ,$targets
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
}

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
