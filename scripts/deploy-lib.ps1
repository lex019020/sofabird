# Shared functions for deploy-windows.ps1 and deploy-android.ps1. Loaded by dot-sourcing:
#   . (Join-Path $PSScriptRoot 'deploy-lib.ps1')
# Compatible with Windows PowerShell 5.1.

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

# ---------------------------------------------------------------- GitHub Releases
# The repository is public: the REST API and the release files are read anonymously (the API allows 60 requests
# an hour per IP; a deploy makes two). If that fails (no network to the API, the limit is used up, the repository
# is private) and the GitHub CLI is installed and logged in, the same requests go through gh.

function Test-GhReady {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $false }
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script
    & gh auth status *> $null
    return $LASTEXITCODE -eq 0
}

function Invoke-ReleasesApi([string]$Repo, [string]$Path) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'User-Agent' = 'vlc-remote-deploy'; 'Accept' = 'application/vnd.github+json' }
        $result = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/$Path" -Headers $headers -UseBasicParsing
        return [pscustomobject]@{ Data = $result; ViaGh = $false }
    } catch {
        $reason = $_.Exception.Message
    }
    if (-not (Test-GhReady)) {
        throw "Could not read GitHub releases of $Repo ($reason). Check the network, or install the GitHub CLI and run gh auth login."
    }
    Write-Note "GitHub API is not reachable anonymously ($reason); using gh"
    $ErrorActionPreference = 'Continue'
    $json = & gh api "repos/$Repo/$Path"
    if ($LASTEXITCODE -ne 0) { throw "gh api repos/$Repo/$Path failed" }
    # PS 5.1: ConvertFrom-Json returns a JSON array as a single object - the callers unroll it with @().
    return [pscustomobject]@{ Data = (($json -join "`n") | ConvertFrom-Json); ViaGh = $true }
}

function Resolve-ReleaseTag([string]$Repo, [string]$Channel, [string]$Tag) {
    if ($Tag) { return $Tag }
    $all = (Invoke-ReleasesApi $Repo 'releases?per_page=50').Data
    $releases = @($all) | ForEach-Object { $_ } | Where-Object { -not $_.draft }
    if ($Channel -eq 'stable') { $releases = $releases | Where-Object { -not $_.prerelease } }
    $latest = $releases | Sort-Object { [datetime]$_.published_at } -Descending | Select-Object -First 1
    if (-not $latest) { throw "There are no builds in channel '$Channel'" }
    return $latest.tag_name
}

function Get-ReleaseAsset([string]$Repo, [string]$ReleaseTag, [string]$Pattern, [string]$Dest) {
    $release = Invoke-ReleasesApi $Repo "releases/tags/$ReleaseTag"
    $asset = @($release.Data.assets) | Where-Object { $_.name -like $Pattern } | Select-Object -First 1
    if (-not $asset) { throw "Release $ReleaseTag has no file $Pattern" }
    $file = Join-Path $Dest $asset.name
    Write-Step "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    if ($release.ViaGh) {
        $ErrorActionPreference = 'Continue'
        & gh release download $ReleaseTag --repo $Repo --dir $Dest --clobber --pattern $asset.name
        if ($LASTEXITCODE -ne 0) { throw "Could not download $($asset.name) from release $ReleaseTag" }
    } else {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing -Headers @{ 'User-Agent' = 'vlc-remote-deploy' }
    }
    if (-not (Test-Path $file)) { throw "Could not download $($asset.name) from release $ReleaseTag" }
    return $file
}

# ---------------------------------------------------------------- companion (Windows)

# Publishes the companion and packs it with Velopack into $ReleasesDir (Setup.exe, the full and, when the previous
# package is already there, a delta update). The release workflow and -Local both use it, so the package is the same.
function New-CompanionPackage([string]$Root, [string]$Version, [string]$Suffix, [string]$ReleasesDir) {
    $ErrorActionPreference = 'Continue'  # PS 5.1: an external program's stderr must not abort the script; we check the exit code/output
    $out = Join-Path ([IO.Path]::GetTempPath()) ('vlcremote-publish-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        & dotnet publish (Join-Path $Root 'companion\VlcRemote.Companion\VlcRemote.Companion.csproj') `
            -c Release -r win-x64 --self-contained false "-p:VersionSuffix=$Suffix" -o $out --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
        Push-Location $Root
        try {
            & dotnet tool restore | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed (vpk)' }
            & dotnet vpk pack --runtime win-x64 --packId VlcRemoteCompanion --packVersion $Version --packDir $out `
                --mainExe VlcRemote.Companion.exe --packTitle 'VLC Remote Companion' --packAuthors 'VLC Remote' `
                --shortcuts StartMenuRoot --framework 'net9-x64-desktop,net9-x64-aspnetcore' -o $ReleasesDir | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
        } finally { Pop-Location }
    } finally {
        Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    }
    return Join-Path $ReleasesDir 'VlcRemoteCompanion-win-Setup.exe'
}

function Invoke-LocalCompanionBuild([string]$Dest) {
    $v = New-LocalVersion
    Write-Step "Building the companion $($v.Version)"
    $setup = New-CompanionPackage $v.Root $v.Version $v.Suffix (Join-Path $Dest 'releases')
    return [pscustomobject]@{ Version = $v.Version; File = $setup }
}

# ---------------------------------------------------------------- Android

function Invoke-LocalApkBuild([string]$Dest) {
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
