# Shared functions for deploy-windows.ps1 and deploy-android.ps1. Loaded by dot-sourcing:
#   . (Join-Path $PSScriptRoot 'deploy-lib.ps1')
# Compatible with Windows PowerShell 5.1. Uses the calling script's variables: $Repo, $Channel, $Tag.

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

# ---------------------------------------------------------------- build source

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if ((Test-Path (Join-Path $dir 'VERSION')) -and (Test-Path (Join-Path $dir 'companion'))) { return $dir }
        $dir = Split-Path $dir -Parent
    }
    throw 'Repository root (the VERSION file) not found. -Local works only from a working copy.'
}

function New-LocalVersion {
    $root = Get-RepoRoot
    $base = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim()
    $suffix = 'local.' + (Get-Date -Format 'yyyyMMddHHmm')
    return [pscustomobject]@{ Root = $root; Suffix = $suffix; Version = "$base-$suffix" }
}

function Assert-Gh {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI is required: winget install --id GitHub.cli -e; then gh auth login'
    }
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh is not authenticated: run gh auth login' }
}

function Resolve-ReleaseTag {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    if ($Tag) { return $Tag }
    $json = & gh release list --repo $Repo --limit 50 --json tagName,isPrerelease,isDraft,publishedAt
    if ($LASTEXITCODE -ne 0) { throw "Could not get the list of releases of $Repo" }
    # PS 5.1: ConvertFrom-Json returns a JSON array as a single object - unroll it with @() before filtering.
    $all = ($json -join "`n") | ConvertFrom-Json
    $releases = @($all) | Where-Object { -not $_.isDraft }
    if ($Channel -eq 'stable') { $releases = $releases | Where-Object { -not $_.isPrerelease } }
    $latest = $releases | Sort-Object { [datetime]$_.publishedAt } -Descending | Select-Object -First 1
    if (-not $latest) { throw "There are no builds in channel '$Channel'" }
    return $latest.tagName
}

function Get-ReleaseAsset([string]$ReleaseTag, [string]$Pattern, [string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    & gh release download $ReleaseTag --repo $Repo --dir $Dest --clobber --pattern $Pattern
    if ($LASTEXITCODE -ne 0) { throw "Could not download $Pattern from release $ReleaseTag" }
    $file = Get-ChildItem $Dest -Filter $Pattern | Select-Object -First 1
    if (-not $file) { throw "Release $ReleaseTag has no file $Pattern" }
    return $file.FullName
}

# ---------------------------------------------------------------- companion (Windows)

function Build-LocalCompanion([string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    $v = New-LocalVersion
    Write-Step "Building the companion $($v.Version)"
    $out = Join-Path $Dest 'companion'
    & dotnet publish (Join-Path $v.Root 'companion\VlcRemote.Companion\VlcRemote.Companion.csproj') `
        -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
        "-p:VersionSuffix=$($v.Suffix)" -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $zip = Join-Path $Dest "VlcRemote.Companion-$($v.Version)-win-x64.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    return [pscustomobject]@{ Version = $v.Version; File = $zip }
}

# ---------------------------------------------------------------- Android

function Build-LocalApk([string]$Dest) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    $v = New-LocalVersion
    Write-Step "Building the APK $($v.Version)"
    if (-not (Test-Path (Join-Path $v.Root 'android\keystore.properties')) -and -not $env:VLCR_KEYSTORE_FILE) {
        throw 'No android\keystore.properties - without the shared key the APK will not install over the test build. See CLAUDE.md, "APK signing".'
    }
    if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
        $jdk = @('C:\Program Files\Android\Android Studio\jbr') +
            @(Get-ChildItem 'C:\Program Files\Android\openjdk', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Microsoft' -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'jdk-(17|21)' } | Sort-Object Name -Descending | ForEach-Object { $_.FullName }) |
            Where-Object { Test-Path (Join-Path $_ 'bin\java.exe') } | Select-Object -First 1
        if (-not $jdk) { throw 'JDK 17/21 not found. Set JAVA_HOME.' }
        $env:JAVA_HOME = $jdk
        Write-Note "JAVA_HOME = $jdk"
    }
    # versionCode: CI = commits in main x 10; local = commits in origin/main x 10 + 5 - newer than the current
    # test build but older than the next one (a branch's commit count is not preserved after a squash merge).
    $mainCount = & git -C $v.Root rev-list --count origin/main 2>$null
    if (-not $mainCount) { $mainCount = & git -C $v.Root rev-list --count HEAD }
    $code = [int]$mainCount * 10 + 5
    Push-Location (Join-Path $v.Root 'android')
    try {
        # No daemons: otherwise the Gradle/Kotlin daemon inherits stdout and the calling process waits for the output to close.
        & .\gradlew.bat :app:assembleRelease "-Pvlcr.versionCode=$code" "-Pvlcr.versionSuffix=$($v.Suffix)" -q --no-daemon "-Pkotlin.compiler.execution.strategy=in-process"
        if ($LASTEXITCODE -ne 0) { throw 'Gradle failed' }
    } finally { Pop-Location }
    $apk = Join-Path $Dest "VlcRemote-$($v.Version).apk"
    Copy-Item (Join-Path $v.Root 'android\app\build\outputs\apk\release\app-release.apk') $apk
    return [pscustomobject]@{ Version = $v.Version; File = $apk }
}

function Find-Adb {
    $adb = Find-AdbPath
    if (-not $script:AdbServerStarted) {
        # Start the adb server as a separate process that does not inherit our stdout/stderr: otherwise the daemon
        # started by `adb devices` keeps the output pipe open and the calling process waits forever.
        # No -Wait: in Windows PowerShell it also waits for child processes, and the adb daemon keeps running.
        # adb's built-in mDNS discovery (Openscreen): phones paired over Wi-Fi are found without a port.
        $env:ADB_MDNS_OPENSCREEN = '1'
        $p = Start-Process -FilePath $adb -ArgumentList 'start-server' -WindowStyle Hidden -PassThru
        [void]$p.WaitForExit(15000)
        $script:AdbServerStarted = $true
    }
    return $adb
}

function Find-AdbPath {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    foreach ($v in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) { if ($v) { $candidates += (Join-Path $v 'platform-tools\adb.exe') } }
    $candidates += 'D:\Android\android-sdk\platform-tools\adb.exe'
    $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw 'adb not found. Install Android platform-tools: winget install --id Google.PlatformTools -e'
}
