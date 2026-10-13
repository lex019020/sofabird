<#
.SYNOPSIS
  Установка и обновление VLC Remote: компаньон для Windows и (по желанию) APK на телефон.

.DESCRIPTION
  Источник сборки:
    * GitHub Releases (по умолчанию). Канал beta — самая свежая сборка (тестовые + релизы),
      канал stable — последний релиз. Нужен GitHub CLI (gh) с входом в аккаунт, у которого
      есть доступ к репозиторию.
    * -Local — собрать из текущей рабочей копии репозитория (dotnet + Gradle).

  Компаньон ставится в %LOCALAPPDATA%\VlcRemote\app без прав администратора. Предыдущая
  версия сохраняется в app.prev (откат: -Rollback). Настройки, сопряжённые телефоны и журналы
  лежат в %LOCALAPPDATA%\VlcRemote и при обновлении не трогаются.

  APK ставится ТОЛЬКО на устройства из списка разрешённых (%USERPROFILE%\.vlc-remote\devices.json,
  по серийному номеру ro.serialno). Остальные подключённые по adb устройства (например, велокомпьютер)
  пропускаются.

.EXAMPLE
  .\deploy.ps1                       # последняя тестовая сборка компаньона
  .\deploy.ps1 -Apk                  # + APK на разрешённые телефоны, подключённые по adb
  .\deploy.ps1 -ListDevices          # какие устройства подключены и какие разрешены
  .\deploy.ps1 -AllowDevice RFCWC0SSKZK   # разрешить установку APK на устройство (серийный номер)
  .\deploy.ps1 -Channel stable       # последний релиз
  .\deploy.ps1 -Tag v0.2.0-beta.12   # конкретная сборка
  .\deploy.ps1 -Local -Apk           # собрать из исходников и поставить
  .\deploy.ps1 -CheckOnly            # только сравнить версии
  .\deploy.ps1 -Firewall             # + правила брандмауэра (один запрос UAC)
  .\deploy.ps1 -Rollback             # вернуть предыдущую версию компаньона
  .\deploy.ps1 -Uninstall            # удалить компаньон (данные остаются; -Purge — удалить и их)
#>
[CmdletBinding()]
param(
    [ValidateSet('beta', 'stable')]
    [string]$Channel = 'beta',
    [string]$Tag,
    [switch]$Local,
    [switch]$Apk,
    [switch]$ApkOnly,
    [switch]$Autostart,
    [switch]$Firewall,
    [switch]$CheckOnly,
    [switch]$Rollback,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoStart,
    [switch]$ListDevices,
    [string]$AllowDevice,
    [string]$DisallowDevice,
    [string]$DevicesFile = (Join-Path $env:USERPROFILE '.vlc-remote\devices.json'),
    [string]$Repo = 'lex019020/vlc-remote',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'VlcRemote')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ExeName      = 'VlcRemote.Companion.exe'
$AppDir       = Join-Path $InstallRoot 'app'
$PrevDir      = Join-Path $InstallRoot 'app.prev'
$VersionFile  = Join-Path $AppDir 'version.txt'
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue     = 'VlcRemoteCompanion'
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'VLC Remote Companion.lnk'
$PackageId    = 'dev.vlcremote.app'
$DefaultRoot  = Join-Path $env:LOCALAPPDATA 'VlcRemote'
# Каталог данных компаньона совпадает с InstallRoot; для нестандартного пути передаём его явно.
$DataArgs     = @()
if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('') -ne [IO.Path]::GetFullPath($DefaultRoot).TrimEnd('')) {
    $DataArgs = @('--data-dir', $InstallRoot)
}
$DataArgString = ($DataArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

function Get-InstalledVersion {
    if (Test-Path $VersionFile) { return (Get-Content $VersionFile -TotalCount 1).Trim() }
    return $null
}

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if ((Test-Path (Join-Path $dir 'VERSION')) -and (Test-Path (Join-Path $dir 'companion'))) { return $dir }
        $dir = Split-Path $dir -Parent
    }
    throw 'Не найден корень репозитория (файл VERSION). -Local работает только из рабочей копии.'
}

function Assert-Gh {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "Нужен GitHub CLI: winget install --id GitHub.cli -e; затем gh auth login"
    }
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh не авторизован: выполните gh auth login' }
}

function Resolve-ReleaseTag {
    if ($Tag) { return $Tag }
    $json = & gh release list --repo $Repo --limit 50 --json tagName,isPrerelease,isDraft,publishedAt
    if ($LASTEXITCODE -ne 0) { throw "Не удалось получить список релизов $Repo" }
    $releases = $json | ConvertFrom-Json | Where-Object { -not $_.isDraft }
    if ($Channel -eq 'stable') { $releases = $releases | Where-Object { -not $_.isPrerelease } }
    $latest = $releases | Sort-Object { [datetime]$_.publishedAt } -Descending | Select-Object -First 1
    if (-not $latest) { throw "В канале '$Channel' нет ни одной сборки" }
    return $latest.tagName
}

function Get-ReleaseAssets([string]$ReleaseTag, [string]$Dest) {
    $patterns = @()
    if (-not $ApkOnly) { $patterns += 'VlcRemote.Companion-*-win-x64.zip' }
    if ($Apk -or $ApkOnly) { $patterns += 'VlcRemote-*.apk' }
    $ghArgs = @('release', 'download', $ReleaseTag, '--repo', $Repo, '--dir', $Dest, '--clobber')
    foreach ($p in $patterns) { $ghArgs += @('--pattern', $p) }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "Не удалось скачать файлы релиза $ReleaseTag" }
}

function Build-Local([string]$Dest) {
    $root = Get-RepoRoot
    $stamp = Get-Date -Format 'yyyyMMddHHmm'
    $base = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim()
    $suffix = "local.$stamp"
    $version = "$base-$suffix"
    if (-not $ApkOnly) {
        Write-Step "Сборка компаньона $version"
        $out = Join-Path $Dest 'companion'
        & dotnet publish (Join-Path $root 'companion\VlcRemote.Companion\VlcRemote.Companion.csproj') `
            -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
            "-p:VersionSuffix=$suffix" -o $out --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath (Join-Path $Dest "VlcRemote.Companion-$version-win-x64.zip") -Force
    }
    if ($Apk -or $ApkOnly) {
        Write-Step "Сборка APK $version"
        if (-not (Test-Path (Join-Path $root 'android\keystore.properties')) -and -not $env:VLCR_KEYSTORE_FILE) {
            throw 'Нет android\keystore.properties — без общего ключа APK не встанет поверх тестовой сборки. См. CLAUDE.md, раздел «Подпись APK».'
        }
        $code = [int](& git -C $root rev-list --count HEAD)
        Push-Location (Join-Path $root 'android')
        try {
            & .\gradlew.bat :app:assembleRelease "-Pvlcr.versionCode=$code" "-Pvlcr.versionSuffix=$suffix" -q
            if ($LASTEXITCODE -ne 0) { throw 'Gradle завершился с ошибкой' }
        } finally { Pop-Location }
        Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $Dest "VlcRemote-$version.apk")
    }
    return "v$version"
}

function Test-Runtimes {
    $list = @()
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { $list = & dotnet --list-runtimes 2>$null }
    $missing = @()
    if (-not ($list | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 9\.' })) { $missing += 'Microsoft.DotNet.DesktopRuntime.9' }
    if (-not ($list | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 9\.' }))      { $missing += 'Microsoft.DotNet.AspNetCore.9' }
    if ($missing.Count -eq 0) { return }
    Write-Step 'Установка .NET 9 Runtime (нужен компаньону)'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Не хватает: $($missing -join ', '). Установите .NET 9 Desktop Runtime и ASP.NET Core Runtime с https://dotnet.microsoft.com/download/dotnet/9.0"
    }
    foreach ($id in $missing) {
        & winget install --id $id -e --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget не смог установить $id" }
    }
}

function Stop-Companion {
    $exe = Join-Path $AppDir $ExeName
    if (Test-Path $exe) {
        & $exe --shutdown @DataArgs | Out-Null
    }
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $procs = Get-Process -Name 'VlcRemote.Companion' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($InstallRoot, [StringComparison]::OrdinalIgnoreCase) }
        if (-not $procs) { return }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    Write-Note 'Компаньон не завершился сам, останавливаю принудительно'
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Set-Shortcut([string]$Exe) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($ShortcutPath)
    $lnk.TargetPath = $Exe
    $lnk.Arguments = $DataArgString
    $lnk.WorkingDirectory = Split-Path $Exe
    $lnk.Description = 'VLC Remote Companion'
    $lnk.Save()
}

function Update-Autostart([string]$Exe) {
    $current = (Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue).$RunValue
    if ($Autostart -or $current) {
        Set-ItemProperty -Path $RunKey -Name $RunValue -Value ("`"$Exe`" --autostart " + $DataArgString).Trim()
        Write-Ok 'Автозапуск при входе в Windows включён'
    }
}

function Add-FirewallRules([string]$Exe) {
    $vlc = $null
    try { $vlc = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\VideoLAN\VLC' -ErrorAction Stop).InstallDir 'vlc.exe' } catch {}
    $cmds = @(
        "netsh advfirewall firewall delete rule name=`"VLC Remote Companion`"",
        "netsh advfirewall firewall add rule name=`"VLC Remote Companion`" dir=in action=allow profile=private program=`"$Exe`" enable=yes"
    )
    if ($vlc -and (Test-Path $vlc)) {
        $cmds += "netsh advfirewall firewall delete rule name=`"VLC (VLC Remote)`""
        $cmds += "netsh advfirewall firewall add rule name=`"VLC (VLC Remote)`" dir=in action=allow profile=private program=`"$vlc`" enable=yes"
    }
    Write-Step 'Правила брандмауэра (частная сеть) — Windows запросит права администратора'
    $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', ($cmds -join ' & ') -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0) { Write-Ok 'Правила добавлены' } else { Write-Note "netsh вернул код $($p.ExitCode)" }
}

function Get-CompanionPort {
    $settings = Join-Path $InstallRoot 'settings.json'
    if (Test-Path $settings) {
        try { $s = Get-Content $settings -Raw | ConvertFrom-Json; if ($s.wsPort) { return [int]$s.wsPort } } catch {}
    }
    return 47555
}

function Start-Companion([string]$Exe) {
    if ($DataArgs.Count -gt 0) { Start-Process -FilePath $Exe -ArgumentList $DataArgString -WorkingDirectory (Split-Path $Exe) }
    else { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) }
    $port = Get-CompanionPort
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try {
            $info = Invoke-RestMethod -Uri "http://127.0.0.1:$port/info" -TimeoutSec 2
            Write-Ok "Компаньон $($info.version) запущен, порт $port; VLC: $(if ($info.vlc.running) { $info.vlc.version } else { 'не запущен' })"
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    Write-Note "Компаньон не ответил на http://127.0.0.1:$port/info за 15 с — см. журнал в $InstallRoot\logs"
}

function Install-Companion([string]$Zip, [string]$Version) {
    Test-Runtimes
    Write-Step "Установка компаньона $Version в $AppDir"
    $staging = Join-Path $InstallRoot 'app.new'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    Expand-Archive -Path $Zip -DestinationPath $staging -Force
    if (-not (Test-Path (Join-Path $staging $ExeName))) { throw "В архиве нет $ExeName" }
    Set-Content -Path (Join-Path $staging 'version.txt') -Value $Version -Encoding ascii

    Stop-Companion
    if (Test-Path $PrevDir) { Remove-Item $PrevDir -Recurse -Force }
    if (Test-Path $AppDir)  { Move-Item $AppDir $PrevDir }
    Move-Item $staging $AppDir

    $exe = Join-Path $AppDir $ExeName
    Set-Shortcut $exe
    Update-Autostart $exe
    if ($Firewall) { Add-FirewallRules $exe }
    elseif (-not (Get-NetFirewallApplicationFilter -Program $exe -ErrorAction SilentlyContinue)) {
        Write-Note 'Правила брандмауэра для компаньона нет. При первом запуске Windows спросит доступ — разрешите для частных сетей (или запустите с -Firewall).'
    }
    if (-not $NoStart) { Start-Companion $exe }
}

function Find-Adb {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    foreach ($v in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) { if ($v) { $candidates += (Join-Path $v 'platform-tools\adb.exe') } }
    $candidates += 'D:\Android\android-sdk\platform-tools\adb.exe'
    $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw 'adb не найден. Установите Android platform-tools: winget install --id Google.PlatformTools -e'
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
    # Устройства в состоянии "device" с их настоящим серийным номером (у Wi‑Fi‑отладки id = ip:port).
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

function Show-Devices {
    $allowed = Get-AllowedDevices
    Write-Step "Разрешённые устройства ($DevicesFile)"
    if ($allowed.Count -eq 0) { Write-Note 'список пуст' }
    foreach ($a in $allowed) { Write-Host "    $($a.serial)  $($a.name)" }
    Write-Step 'Подключённые по adb'
    $connected = Get-ConnectedDevices
    if ($connected.Count -eq 0) { Write-Note 'нет' }
    foreach ($d in $connected) {
        $ok = [bool]($allowed | Where-Object { $_.serial -eq $d.Serial })
        $mark = if ($ok) { '[разрешено]' } else { '[пропускается]' }
        Write-Host "    $($d.Serial)  $($d.Model)  ($($d.Id))  $mark"
    }
}

function Install-Apk([string]$ApkPath) {
    Write-Step "Установка $(Split-Path $ApkPath -Leaf) на телефон"
    $allowed = Get-AllowedDevices
    if ($allowed.Count -eq 0) {
        throw "Список разрешённых устройств пуст ($DevicesFile). Подключите телефон, посмотрите его серийный номер (-ListDevices) и добавьте: -AllowDevice <серийный номер>."
    }
    $adb = Find-Adb
    $connected = Get-ConnectedDevices
    $targets = @()
    foreach ($d in $connected) {
        if ($allowed | Where-Object { $_.serial -eq $d.Serial }) { $targets += $d }
        else { Write-Note "Пропущено: $($d.Model) ($($d.Serial)) — нет в списке разрешённых" }
    }
    if ($targets.Count -eq 0) {
        Write-Note "Разрешённый телефон не подключён ($(($allowed | ForEach-Object { $_.name }) -join ', ')). Отладка по Wi‑Fi: adb connect <ip>:<порт>, затем -ApkOnly."
        $keep = Join-Path $InstallRoot 'apk'
        if (-not (Test-Path $keep)) { New-Item -ItemType Directory -Path $keep | Out-Null }
        Copy-Item $ApkPath $keep -Force
        Write-Note "APK сохранён: $(Join-Path $keep (Split-Path $ApkPath -Leaf)) — его можно и просто передать на телефон."
        return
    }
    foreach ($t in $targets) {
        $out = & $adb -s $t.Id install -r $ApkPath 2>&1 | Out-String
        if ($out -match 'Success') { Write-Ok "APK установлен: $($t.Model) ($($t.Serial))"; continue }
        if ($out -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
            throw "$($t.Model): стоит сборка с другой подписью. Удалите её (adb -s $($t.Id) uninstall $PackageId — сопряжения и настройки пропадут) и повторите."
        }
        if ($out -match 'INSTALL_FAILED_VERSION_DOWNGRADE') {
            throw "$($t.Model): стоит более новая сборка. Удалите её или поставьте сборку новее."
        }
        throw "adb install ($($t.Model)): $out"
    }
}

# ---------------------------------------------------------------- основное

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

if ($Uninstall) {
    Write-Step 'Удаление компаньона'
    Stop-Companion
    foreach ($d in @($AppDir, $PrevDir)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    if (Test-Path $ShortcutPath) { Remove-Item $ShortcutPath -Force }
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    if ($Purge -and (Test-Path $InstallRoot)) { Remove-Item $InstallRoot -Recurse -Force; Write-Ok 'Настройки и журналы удалены' }
    Write-Ok 'Готово'
    return
}

if ($Rollback) {
    if (-not (Test-Path $PrevDir)) { throw 'Нет предыдущей версии (app.prev)' }
    Write-Step 'Откат на предыдущую версию'
    Stop-Companion
    $tmp = Join-Path $InstallRoot 'app.swap'
    if (Test-Path $AppDir) { Move-Item $AppDir $tmp }
    Move-Item $PrevDir $AppDir
    if (Test-Path $tmp) { Move-Item $tmp $PrevDir }
    Write-Ok "Текущая версия: $(Get-InstalledVersion)"
    $exe = Join-Path $AppDir $ExeName
    Set-Shortcut $exe
    Update-Autostart $exe
    if (-not $NoStart) { Start-Companion $exe }
    return
}

$installed = Get-InstalledVersion
$work = Join-Path ([IO.Path]::GetTempPath()) ("vlcremote-deploy-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($Local) {
        $tagName = Build-Local $work
    } else {
        Assert-Gh
        $tagName = Resolve-ReleaseTag
    }
    $target = $tagName.TrimStart('v')
    Write-Step "Сборка: $target (установлено: $(if ($installed) { $installed } else { 'нет' }))"
    if ($CheckOnly) {
        if ($installed -eq $target) { Write-Ok 'Установлена последняя версия' } else { Write-Note 'Есть обновление' }
        return
    }
    if (-not $Local) { Get-ReleaseAssets $tagName $work }

    if (-not $ApkOnly) {
        $zip = Get-ChildItem $work -Filter 'VlcRemote.Companion-*-win-x64.zip' | Select-Object -First 1
        if (-not $zip) { throw 'Архив компаньона не найден' }
        if ($installed -eq $target -and -not $Local) {
            Write-Ok "Компаньон $target уже установлен"
            if ($Firewall) { Add-FirewallRules (Join-Path $AppDir $ExeName) }
        } else {
            Install-Companion $zip.FullName $target
        }
    }
    if ($Apk -or $ApkOnly) {
        $apkFile = Get-ChildItem $work -Filter 'VlcRemote-*.apk' | Select-Object -First 1
        if (-not $apkFile) { throw 'APK не найден в сборке' }
        Install-Apk $apkFile.FullName
    }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
