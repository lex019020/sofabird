# Общие функции для deploy-windows.ps1 и deploy-android.ps1. Подключается dot-source'ом:
#   . (Join-Path $PSScriptRoot 'deploy-lib.ps1')
# Совместимо с Windows PowerShell 5.1. Использует переменные вызывающего скрипта: $Repo, $Channel, $Tag.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:VlcrDataRoot = Join-Path $env:LOCALAPPDATA 'VlcRemote'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

function New-WorkDir {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("vlcremote-deploy-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    return $dir
}

# ---------------------------------------------------------------- источник сборки

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if ((Test-Path (Join-Path $dir 'VERSION')) -and (Test-Path (Join-Path $dir 'companion'))) { return $dir }
        $dir = Split-Path $dir -Parent
    }
    throw 'Не найден корень репозитория (файл VERSION). -Local работает только из рабочей копии.'
}

function New-LocalVersion {
    $root = Get-RepoRoot
    $base = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim()
    $suffix = 'local.' + (Get-Date -Format 'yyyyMMddHHmm')
    return [pscustomobject]@{ Root = $root; Suffix = $suffix; Version = "$base-$suffix" }
}

function Assert-Gh {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'Нужен GitHub CLI: winget install --id GitHub.cli -e; затем gh auth login'
    }
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh не авторизован: выполните gh auth login' }
}

function Resolve-ReleaseTag {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    if ($Tag) { return $Tag }
    $json = & gh release list --repo $Repo --limit 50 --json tagName,isPrerelease,isDraft,publishedAt
    if ($LASTEXITCODE -ne 0) { throw "Не удалось получить список релизов $Repo" }
    # PS 5.1: ConvertFrom-Json отдаёт JSON-массив одним объектом — разворачиваем его через @() до фильтрации.
    $all = ($json -join "`n") | ConvertFrom-Json
    $releases = @($all) | Where-Object { -not $_.isDraft }
    if ($Channel -eq 'stable') { $releases = $releases | Where-Object { -not $_.isPrerelease } }
    $latest = $releases | Sort-Object { [datetime]$_.publishedAt } -Descending | Select-Object -First 1
    if (-not $latest) { throw "В канале '$Channel' нет ни одной сборки" }
    return $latest.tagName
}

function Get-ReleaseAsset([string]$ReleaseTag, [string]$Pattern, [string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    & gh release download $ReleaseTag --repo $Repo --dir $Dest --clobber --pattern $Pattern
    if ($LASTEXITCODE -ne 0) { throw "Не удалось скачать $Pattern из релиза $ReleaseTag" }
    $file = Get-ChildItem $Dest -Filter $Pattern | Select-Object -First 1
    if (-not $file) { throw "В релизе $ReleaseTag нет файла $Pattern" }
    return $file.FullName
}

# ---------------------------------------------------------------- компаньон (Windows)

function Build-LocalCompanion([string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    $v = New-LocalVersion
    Write-Step "Сборка компаньона $($v.Version)"
    $out = Join-Path $Dest 'companion'
    & dotnet publish (Join-Path $v.Root 'companion\VlcRemote.Companion\VlcRemote.Companion.csproj') `
        -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
        "-p:VersionSuffix=$($v.Suffix)" -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
    $zip = Join-Path $Dest "VlcRemote.Companion-$($v.Version)-win-x64.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    return [pscustomobject]@{ Version = $v.Version; File = $zip }
}

# ---------------------------------------------------------------- Android

function Build-LocalApk([string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: stderr внешней программы не должен прерывать скрипт; проверяем код/вывод
    $v = New-LocalVersion
    Write-Step "Сборка APK $($v.Version)"
    if (-not (Test-Path (Join-Path $v.Root 'android\keystore.properties')) -and -not $env:VLCR_KEYSTORE_FILE) {
        throw 'Нет android\keystore.properties — без общего ключа APK не встанет поверх тестовой сборки. См. CLAUDE.md, «Подпись APK».'
    }
    if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
        $jdk = @('C:\Program Files\Android\Android Studio\jbr') +
            @(Get-ChildItem 'C:\Program Files\Android\openjdk', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Microsoft' -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'jdk-(17|21)' } | Sort-Object Name -Descending | ForEach-Object { $_.FullName }) |
            Where-Object { Test-Path (Join-Path $_ 'bin\java.exe') } | Select-Object -First 1
        if (-not $jdk) { throw 'Не найден JDK 17/21. Задайте JAVA_HOME.' }
        $env:JAVA_HOME = $jdk
        Write-Note "JAVA_HOME = $jdk"
    }
    # versionCode: CI = коммиты в main × 10; локальная = коммиты в origin/main × 10 + 5 — новее текущей
    # тестовой сборки, но старше следующей (число коммитов ветки после squash-мержа не сохраняется).
    $mainCount = & git -C $v.Root rev-list --count origin/main 2>$null
    if (-not $mainCount) { $mainCount = & git -C $v.Root rev-list --count HEAD }
    $code = [int]$mainCount * 10 + 5
    Push-Location (Join-Path $v.Root 'android')
    try {
        # Без демонов: иначе Gradle/Kotlin-демон наследует stdout и вызывающий процесс ждёт закрытия вывода.
        & .\gradlew.bat :app:assembleRelease "-Pvlcr.versionCode=$code" "-Pvlcr.versionSuffix=$($v.Suffix)" -q --no-daemon "-Pkotlin.compiler.execution.strategy=in-process"
        if ($LASTEXITCODE -ne 0) { throw 'Gradle завершился с ошибкой' }
    } finally { Pop-Location }
    $apk = Join-Path $Dest "VlcRemote-$($v.Version).apk"
    Copy-Item (Join-Path $v.Root 'android\app\build\outputs\apk\release\app-release.apk') $apk
    return [pscustomobject]@{ Version = $v.Version; File = $apk }
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
