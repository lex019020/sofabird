<#
.SYNOPSIS
  Installs and updates the VLC Remote companion on Windows.

.DESCRIPTION
  Source: GitHub Releases (default; requires gh with access to the repository) or -Local - a build from the working
  copy. The beta channel is the newest build (test builds + releases), stable is the latest release.

  The companion is installed to %LOCALAPPDATA%\VlcRemote\app without administrator rights. Before the files are
  replaced, the running instance is shut down gracefully (--shutdown). The previous version is kept in app.prev
  (-Rollback). Settings, paired phones and logs live in %LOCALAPPDATA%\VlcRemote and are not touched by an update.
  The phone is handled by a separate script, deploy-android.ps1.

.EXAMPLE
  .\deploy-windows.ps1                       # latest test build
  .\deploy-windows.ps1 -Channel stable       # latest release
  .\deploy-windows.ps1 -Tag v0.2.0-beta.12   # a specific build
  .\deploy-windows.ps1 -Local                # build from source and install
  .\deploy-windows.ps1 -Package <zip>        # install from a ready-made archive (this is how dev-laptop.ps1 installs over SSH)
  .\deploy-windows.ps1 -CheckOnly            # only compare versions
  .\deploy-windows.ps1 -SkipVlc              # do not install VLC when it is missing
  .\deploy-windows.ps1 -Firewall -Autostart  # + firewall rules (one UAC prompt) and autostart
  .\deploy-windows.ps1 -Rollback             # restore the previous version
  .\deploy-windows.ps1 -Uninstall [-Purge]   # uninstall (with -Purge - also settings, pairings, logs)
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
    [switch]$SkipVlc,
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

# The companion's data directory = InstallRoot; for a non-default path we pass it explicitly.
$DataArgs = @()
if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($VlcrDataRoot).TrimEnd('\')) {
    $DataArgs = @('--data-dir', $InstallRoot)
}
$DataArgString = ($DataArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '

# Over SSH, processes run outside the user's desktop: there would be no tray icon and the pairing window would not
# appear. So the companion is started/stopped through a scheduled task in the interactive session.
$InSsh = [bool]($env:SSH_CLIENT -or $env:SSH_CONNECTION)

function Invoke-InUserSession([string]$Exe, [string]$Arguments, [string]$Purpose, [switch]$Wait) {
    $name = "VlcRemote-$Purpose"
    $action = if ($Arguments) { New-ScheduledTaskAction -Execute $Exe -Argument $Arguments -WorkingDirectory (Split-Path $Exe) }
              else { New-ScheduledTaskAction -Execute $Exe -WorkingDirectory (Split-Path $Exe) }
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
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
        Write-Note 'The user is not signed in to Windows on this computer - the companion will start at sign-in (if autostart is enabled).'
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
    Write-Step 'Installing .NET 9 Runtime (required by the companion)'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Missing: $($missing -join ', '). Install .NET 9 Desktop Runtime and ASP.NET Core Runtime from https://dotnet.microsoft.com/download/dotnet/9.0"
    }
    foreach ($id in $missing) {
        & winget install --id $id -e --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget could not install $id" }
    }
}

function Find-Vlc {
    foreach ($key in 'HKLM:\SOFTWARE\VideoLAN\VLC', 'HKLM:\SOFTWARE\WOW6432Node\VideoLAN\VLC') {
        try {
            $dir = (Get-ItemProperty $key -ErrorAction Stop).InstallDir
            if ($dir -and (Test-Path (Join-Path $dir 'vlc.exe'))) { return (Join-Path $dir 'vlc.exe') }
        } catch {}
    }
    foreach ($dir in @("$env:ProgramFiles\VideoLAN\VLC", "${env:ProgramFiles(x86)}\VideoLAN\VLC")) {
        if ($dir -and (Test-Path (Join-Path $dir 'vlc.exe'))) { return (Join-Path $dir 'vlc.exe') }
    }
    return $null
}

function Install-VlcIfMissing {
    # The companion controls the real VLC media player, so a PC without it gets VLC installed (winget, VideoLAN.VLC).
    $ErrorActionPreference = 'Continue'
    $vlc = Find-Vlc
    if ($vlc) { Write-Ok "VLC found: $vlc"; return }
    if ($SkipVlc) { Write-Note 'VLC is not installed (-SkipVlc): the companion will report "VLC not found" until it is installed.'; return }
    if ($InSsh) {
        Write-Note 'VLC is not installed. Its installer needs an administrator prompt on the desktop, which is not possible over SSH: run deploy-windows.ps1 on the PC itself or install VLC from https://www.videolan.org.'
        return
    }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Note 'VLC is not installed and winget is not available: install VLC from https://www.videolan.org (64-bit), then start the companion again.'
        return
    }
    Write-Step 'Installing VLC media player (winget VideoLAN.VLC; Windows will ask for administrator rights)'
    & winget install --id VideoLAN.VLC -e --silent --accept-package-agreements --accept-source-agreements
    $code = $LASTEXITCODE
    $vlc = Find-Vlc
    if ($vlc) { Write-Ok "VLC installed: $vlc" }
    else { Write-Note "VLC installation did not complete (winget exit code $code). Install it from https://www.videolan.org and restart the companion." }
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
    Write-Note 'The companion did not exit on its own, stopping it forcibly'
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
        Write-Ok 'Autostart at Windows sign-in is enabled'
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
    Write-Step 'Firewall rules (private network) - Windows will ask for administrator rights'
    $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', ($cmds -join ' & ') -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0) { Write-Ok 'Rules added' } else { Write-Note "netsh returned code $($p.ExitCode)" }
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
            Write-Ok "Companion $($info.version) is running, port $port; VLC: $(if ($info.vlc.running) { $info.vlc.version } else { 'not running' })"
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    Write-Note "The companion did not answer at http://127.0.0.1:$port/info within 15 s - see the log in $InstallRoot\logs"
}

function Install-Companion([string]$Zip, [string]$Version) {
    Test-Runtimes
    Install-VlcIfMissing
    Write-Step "Installing the companion $Version to $AppDir"
    $staging = Join-Path $InstallRoot 'app.new'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    Expand-Archive -Path $Zip -DestinationPath $staging -Force
    if (-not (Test-Path (Join-Path $staging $ExeName))) { throw "The archive has no $ExeName" }
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
        Write-Note 'There is no firewall rule for the companion. On the first start Windows will ask for access - allow it for private networks (or run with -Firewall).'
    }
    if (-not $NoStart) { Start-Companion $exe }
}

# ---------------------------------------------------------------- main

if (-not (Test-Path $InstallRoot)) { New-Item -ItemType Directory -Path $InstallRoot | Out-Null }

if ($Uninstall) {
    Write-Step 'Uninstalling the companion'
    Stop-Companion
    foreach ($d in @($AppDir, $PrevDir)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    if (Test-Path $ShortcutPath) { Remove-Item $ShortcutPath -Force }
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    if ($Purge -and (Test-Path $InstallRoot)) { Remove-Item $InstallRoot -Recurse -Force; Write-Ok 'Settings and logs removed' }
    Write-Ok 'Done'
    return
}

if ($Rollback) {
    if (-not (Test-Path $PrevDir)) { throw 'There is no previous version (app.prev)' }
    Write-Step 'Rolling back to the previous version'
    Stop-Companion
    $tmp = Join-Path $InstallRoot 'app.swap'
    if (Test-Path $AppDir) { Move-Item $AppDir $tmp }
    Move-Item $PrevDir $AppDir
    if (Test-Path $tmp) { Move-Item $tmp $PrevDir }
    Write-Ok "Current version: $(Get-InstalledVersion)"
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
        if (-not (Test-Path $Package)) { throw "File $Package not found" }
        if ((Split-Path $Package -Leaf) -notmatch '^VlcRemote\.Companion-(.+)-win-x64\.zip$') { throw 'Expected an archive VlcRemote.Companion-<version>-win-x64.zip' }
        $target = $Matches[1]
    } elseif ($Local) {
        $build = Build-LocalCompanion $work
        $target = $build.Version
    } else {
        Assert-Gh
        $tagName = Resolve-ReleaseTag
        $target = $tagName.TrimStart('v')
    }
    Write-Step "Companion: $target (installed: $(if ($installed) { $installed } else { 'none' }))"
    if ($CheckOnly) {
        if ($installed -eq $target) { Write-Ok 'The latest version is installed' } else { Write-Note 'An update is available' }
        return
    }
    if ($installed -eq $target -and -not $Local -and -not $Package) {
        Write-Ok "Companion $target is already installed"
        if ($Firewall) { Add-FirewallRules (Join-Path $AppDir $ExeName) }
        return
    }
    $zip = if ($Package) { (Resolve-Path $Package).Path } elseif ($Local) { $build.File } else { Get-ReleaseAsset $tagName 'VlcRemote.Companion-*-win-x64.zip' $work }
    Install-Companion $zip $target
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
