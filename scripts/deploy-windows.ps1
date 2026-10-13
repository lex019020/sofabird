<#
.SYNOPSIS
  Установка и обновление компаньона VLC Remote на Windows.

.DESCRIPTION
  Источник: GitHub Releases (по умолчанию; нужен gh с доступом к репозиторию) или -Local — сборка из рабочей
  копии. Канал beta — самая свежая сборка (тестовые + релизы), stable — последний релиз.

  Компаньон ставится в %LOCALAPPDATA%\VlcRemote\app без прав администратора. Перед заменой файлов запущенный
  экземпляр закрывается штатно (--shutdown). Предыдущая версия хранится в app.prev (-Rollback). Настройки,
  сопряжённые телефоны и журналы лежат в %LOCALAPPDATA%\VlcRemote и при обновлении не трогаются.
  Телефон — отдельным скриптом deploy-android.ps1.

.EXAMPLE
  .\deploy-windows.ps1                       # последняя тестовая сборка
  .\deploy-windows.ps1 -Channel stable       # последний релиз
  .\deploy-windows.ps1 -Tag v0.2.0-beta.12   # конкретная сборка
  .\deploy-windows.ps1 -Local                # собрать из исходников и поставить
  .\deploy-windows.ps1 -Package <zip>        # поставить из готового архива (так ставит dev-laptop.ps1 по SSH)
  .\deploy-windows.ps1 -CheckOnly            # только сравнить версии
  .\deploy-windows.ps1 -Firewall -Autostart  # + правила брандмауэра (один запрос UAC) и автозапуск
  .\deploy-windows.ps1 -Rollback             # вернуть предыдущую версию
  .\deploy-windows.ps1 -Uninstall [-Purge]   # удалить (с -Purge — и настройки, сопряжения, журналы)
#>
[CmdletBinding()]
param(
    [ValidateSet('beta', 'stable')]
    [string]$Channel = 'beta',
    [string]$Tag,
    [switch]$Local,
    [string]$Package,
    [switch]$CheckOnly,
    [switch]$Autostart,
    [switch]$Firewall,
    [switch]$Rollback,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoStart,
    [string]$Repo = 'lex019020/vlc-remote',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'VlcRemote')
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$ExeName      = 'VlcRemote.Companion.exe'
$AppDir       = Join-Path $InstallRoot 'app'
$PrevDir      = Join-Path $InstallRoot 'app.prev'
$VersionFile  = Join-Path $AppDir 'version.txt'
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue     = 'VlcRemoteCompanion'
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'VLC Remote Companion.lnk'

# Каталог данных компаньона = InstallRoot; для нестандартного пути передаём его явно.
$DataArgs = @()
if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($VlcrDataRoot).TrimEnd('\')) {
    $DataArgs = @('--data-dir', $InstallRoot)
}
$DataArgString = ($DataArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '

# По SSH процессы запускаются вне рабочего стола пользователя: трей-иконки не будет, окно сопряжения не
# появится. Поэтому компаньон запускаем/останавливаем через задачу планировщика в интерактивном сеансе.
$InSsh = [bool]($env:SSH_CLIENT -or $env:SSH_CONNECTION)

function Invoke-InUserSession([string]$Exe, [string]$Arguments, [string]$Purpose, [switch]$Wait) {
    $name = "VlcRemote-$Purpose"
    $action = if ($Arguments) { New-ScheduledTaskAction -Execute $Exe -Argument $Arguments -WorkingDirectory (Split-Path $Exe) }
              else { New-ScheduledTaskAction -Execute $Exe -WorkingDirectory (Split-Path $Exe) }
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    if ($Wait) {
        $deadline = (Get-Date).AddSeconds(20)
        do { Start-Sleep -Milliseconds 400 } while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $deadline)
    } else { Start-Sleep -Seconds 1 }
    $info = Get-ScheduledTaskInfo -TaskName $name
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    if ($info.LastTaskResult -eq 0x41303 -or $info.LastTaskResult -eq 0x8004130F) {
        Write-Note 'Пользователь не вошёл в Windows на этом компьютере — компаньон запустится при входе (если включён автозапуск).'
    }
}

function Get-InstalledVersion {
    if (Test-Path $VersionFile) { return (Get-Content $VersionFile -TotalCount 1).Trim() }
    return $null
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
        if ($InSsh) { Invoke-InUserSession $exe ((@('--shutdown') + $DataArgs | ForEach-Object { '"' + $_ + '"' }) -join ' ') 'Shutdown' -Wait }
        else { & $exe --shutdown @DataArgs | Out-Null }
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
    if ($InSsh) { Invoke-InUserSession $Exe $DataArgString 'Start' }
    elseif ($DataArgs.Count -gt 0) { Start-Process -FilePath $Exe -ArgumentList $DataArgString -WorkingDirectory (Split-Path $Exe) }
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

# ---------------------------------------------------------------- основное

if (-not (Test-Path $InstallRoot)) { New-Item -ItemType Directory -Path $InstallRoot | Out-Null }

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
$work = New-WorkDir
try {
    if ($Package) {
        if (-not (Test-Path $Package)) { throw "Нет файла $Package" }
        if ((Split-Path $Package -Leaf) -notmatch '^VlcRemote\.Companion-(.+)-win-x64\.zip$') { throw 'Ожидается архив VlcRemote.Companion-<версия>-win-x64.zip' }
        $target = $Matches[1]
    } elseif ($Local) {
        $build = Build-LocalCompanion $work
        $target = $build.Version
    } else {
        Assert-Gh
        $tagName = Resolve-ReleaseTag
        $target = $tagName.TrimStart('v')
    }
    Write-Step "Компаньон: $target (установлено: $(if ($installed) { $installed } else { 'нет' }))"
    if ($CheckOnly) {
        if ($installed -eq $target) { Write-Ok 'Установлена последняя версия' } else { Write-Note 'Есть обновление' }
        return
    }
    if ($installed -eq $target -and -not $Local -and -not $Package) {
        Write-Ok "Компаньон $target уже установлен"
        if ($Firewall) { Add-FirewallRules (Join-Path $AppDir $ExeName) }
        return
    }
    $zip = if ($Package) { (Resolve-Path $Package).Path } elseif ($Local) { $build.File } else { Get-ReleaseAsset $tagName 'VlcRemote.Companion-*-win-x64.zip' $work }
    Install-Companion $zip $target
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
