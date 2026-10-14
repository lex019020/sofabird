<#
.SYNOPSIS
  Installs and updates the Sofabird companion on Windows.

.DESCRIPTION
  The companion is installed by its Velopack installer (Sofabird-Setup.exe from GitHub Releases, or one
  built from the working copy with -Local) into %LOCALAPPDATA%\Sofabird, for the current user, and then
  updates itself. This script runs that installer quietly and adds what it does not do: VLC when it is missing, the
  firewall rules and autostart. Settings, paired phones, history and logs stay in %APPDATA%\Sofabird.
  The companion under its earlier name, VLC Remote Companion (%LOCALAPPDATA%\VlcRemoteCompanion, or the script
  install in %LOCALAPPDATA%\VlcRemote\app), is stopped and uninstalled; Sofabird takes its settings and history over
  at its first start.
  The phone is handled by a separate script, deploy-android.ps1.

.EXAMPLE
  .\deploy-windows.ps1                       # latest test build
  .\deploy-windows.ps1 -Channel stable       # latest release
  .\deploy-windows.ps1 -Tag v0.2.0-beta0040   # a specific build (also the way back to an earlier version)
  .\deploy-windows.ps1 -Local                # build from source and install
  .\deploy-windows.ps1 -Package <Setup.exe>  # install a ready-made installer (this is how dev-laptop.ps1 installs over SSH)
  .\deploy-windows.ps1 -CheckOnly            # only compare versions
  .\deploy-windows.ps1 -SkipVlc              # do not install VLC when it is missing
  .\deploy-windows.ps1 -Firewall -Autostart  # + firewall rules (one UAC prompt) and autostart
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
    [switch]$Uninstall,
    [switch]$Purge,
    [string]$Repo = 'lex019020/sofabird'
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$ExeName     = 'Sofabird.exe'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'Sofabird'   # Velopack: Update.exe, current\, packages\
$Exe         = Join-Path $InstallRoot "current\$ExeName"
$DataRoot    = $SofabirdDataRoot                        # settings, pairings, logs (an update never touches them)
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'Sofabird'
# The companion under its earlier name, VLC Remote Companion: its Velopack install and the older script install.
$LegacyInstallRoot = Join-Path $env:LOCALAPPDATA 'VlcRemoteCompanion'
$LegacyDirs        = @('app', 'app.prev', 'app.new') | ForEach-Object { Join-Path $env:LOCALAPPDATA "VlcRemote\$_" }
$LegacyRunValue    = 'VlcRemoteCompanion'
$LegacyRuleNames   = @('VLC Remote Companion', 'VLC (VLC Remote)')

# Over SSH, processes run outside the user's desktop: there would be no tray icon and the pairing window would not
# appear. So the installer (which starts the companion) and the shutdown run through a scheduled task in the
# interactive session.
$InSsh = [bool]($env:SSH_CLIENT -or $env:SSH_CONNECTION)

function Invoke-InUserSession([string]$File, [string]$Arguments, [string]$Purpose, [int]$WaitSeconds = 0) {
    $name = "Sofabird-$Purpose"
    $action = if ($Arguments) { New-ScheduledTaskAction -Execute $File -Argument $Arguments -WorkingDirectory (Split-Path $File) }
              else { New-ScheduledTaskAction -Execute $File -WorkingDirectory (Split-Path $File) }
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    if ($WaitSeconds -gt 0) {
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        do { Start-Sleep -Milliseconds 400 } while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $deadline)
    } else { Start-Sleep -Seconds 1 }
    $info = Get-ScheduledTaskInfo -TaskName $name
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    if ($info.LastTaskResult -eq 0x41303 -or $info.LastTaskResult -eq 0x8004130F) {
        Write-Note 'The user is not signed in to Windows on this computer: sign in and run the script again.'
    }
}

function Get-InstalledVersion {
    if (Test-Path $Exe) { return (Get-Item $Exe).VersionInfo.ProductVersion }
    $legacy = Join-Path $DataRoot 'app\version.txt'
    if (Test-Path $legacy) { return (Get-Content $legacy -TotalCount 1).Trim() + ' (script install)' }
    return $null
}

function Find-Vlc {
    foreach ($key in 'HKLM:\SOFTWARE\VideoLAN\VLC', 'HKLM:\SOFTWARE\WOW6432Node\VideoLAN\VLC') {
        try {
            $dir = (Get-ItemProperty $key -ErrorAction Stop).InstallDir
            if ($dir -and (Test-Path (Join-Path $dir 'vlc.exe'))) { return (Join-Path $dir 'vlc.exe') }
        } catch { Write-Verbose "No VLC registration under $key" }
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
    $exes = @($Exe, (Join-Path $DataRoot "app\$ExeName")) | Where-Object { Test-Path $_ }
    foreach ($e in $exes) {
        if ($InSsh) { Invoke-InUserSession $e '--shutdown' 'Shutdown' -WaitSeconds 20 }
        else { & $e --shutdown | Out-Null }
    }
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $procs = Get-Process -Name 'Sofabird' -ErrorAction SilentlyContinue
        if (-not $procs) { return }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    Write-Note 'The companion did not exit on its own, stopping it forcibly'
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Remove-LegacyInstall {
    # Both earlier installs run as VlcRemote.Companion.exe and would hold the same ports as Sofabird.
    $procs = Get-Process -Name 'VlcRemote.Companion' -ErrorAction SilentlyContinue
    if ($procs) { $procs | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
    # Read the old autostart first: the old uninstaller removes it.
    if ((Get-ItemProperty -Path $RunKey -Name $LegacyRunValue -ErrorAction SilentlyContinue).$LegacyRunValue) {
        Remove-ItemProperty -Path $RunKey -Name $LegacyRunValue
        $script:Autostart = $true   # it was on for the old name: keep it on for Sofabird
    }
    $found = $false
    foreach ($d in $LegacyDirs) { if (Test-Path $d) { Remove-Item $d -Recurse -Force; $found = $true } }
    $update = Join-Path $LegacyInstallRoot 'Update.exe'
    if (Test-Path $update) {
        $p = Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -Wait -PassThru
        if ($p.ExitCode -ne 0) { Write-Note "The uninstaller of VLC Remote Companion exited with code $($p.ExitCode)" }
        $found = $true
    }
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'VLC Remote Companion.lnk'
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
    if ($found) { Write-Ok 'VLC Remote Companion (the earlier name) is uninstalled; Sofabird takes its settings and history over' }
}

function Update-Autostart {
    $current = (Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue).$RunValue
    if ($Autostart -or $current) {
        Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$Exe`" --autostart"
        Write-Ok 'Autostart at Windows sign-in is enabled'
    }
}

function Add-FirewallRules {
    $vlc = Find-Vlc
    $cmds = @($LegacyRuleNames | ForEach-Object { "netsh advfirewall firewall delete rule name=`"$_`"" })
    $cmds += @(
        "netsh advfirewall firewall delete rule name=`"Sofabird`"",
        "netsh advfirewall firewall add rule name=`"Sofabird`" dir=in action=allow profile=private program=`"$Exe`" enable=yes"
    )
    if ($vlc) {
        $cmds += "netsh advfirewall firewall delete rule name=`"VLC (Sofabird)`""
        $cmds += "netsh advfirewall firewall add rule name=`"VLC (Sofabird)`" dir=in action=allow profile=private program=`"$vlc`" enable=yes"
    }
    Write-Step 'Firewall rules (private network) - Windows will ask for administrator rights'
    $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', ($cmds -join ' & ') -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0) { Write-Ok 'Rules added' } else { Write-Note "netsh returned code $($p.ExitCode)" }
}

function Get-CompanionPort {
    $settings = Join-Path $DataRoot 'settings.json'
    if (Test-Path $settings) {
        try { $s = Get-Content $settings -Raw | ConvertFrom-Json; if ($s.wsPort) { return [int]$s.wsPort } }
        catch { Write-Verbose "Unreadable $settings; the default port is used" }
    }
    return 47555
}

function Start-Companion {
    # A silent install does not start the app; a second instance would only show "already running".
    Start-Sleep -Seconds 2
    if (Get-Process -Name 'Sofabird' -ErrorAction SilentlyContinue) { return }
    if ($InSsh) { Invoke-InUserSession $Exe '' 'Start' }
    else { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) }
}

function Wait-Companion {
    $port = Get-CompanionPort
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        try {
            $info = Invoke-RestMethod -Uri "http://127.0.0.1:$port/info" -TimeoutSec 2
            Write-Ok "Companion $($info.version) is running, port $port; VLC: $(if ($info.vlc.running) { $info.vlc.version } else { 'not running' })"
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    Write-Note "The companion did not answer at http://127.0.0.1:$port/info within 30 s - see the log in $DataRoot\logs"
}

function Install-Companion([string]$Setup, [string]$Version) {
    Install-VlcIfMissing
    Write-Step "Installing the companion $Version"
    Stop-Companion
    Remove-LegacyInstall
    # The installer replaces the installed version (newer or older) and installs the .NET 10 runtime when it is missing.
    if ($InSsh) { Invoke-InUserSession $Setup '--silent' 'Setup' -WaitSeconds 180 }
    else {
        $p = Start-Process -FilePath $Setup -ArgumentList '--silent' -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "The installer exited with code $($p.ExitCode)" }
    }
    if (-not (Test-Path $Exe)) { throw "The installer did not create $Exe" }
    Update-Autostart
    if ($Firewall) { Add-FirewallRules }
    elseif (-not (Get-NetFirewallApplicationFilter -Program $Exe -ErrorAction SilentlyContinue)) {
        Write-Note 'There is no firewall rule for the companion at its new place: the setup window offers to add it (or run with -Firewall).'
    }
    Start-Companion
    Wait-Companion
}

# ---------------------------------------------------------------- main

if ($Uninstall) {
    Write-Step 'Uninstalling the companion'
    Stop-Companion
    $update = Join-Path $InstallRoot 'Update.exe'
    if (Test-Path $update) {
        $p = Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -Wait -PassThru
        if ($p.ExitCode -ne 0) { Write-Note "The uninstaller exited with code $($p.ExitCode)" }
    }
    Remove-LegacyInstall
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    if ($Purge -and (Test-Path $DataRoot)) { Remove-Item $DataRoot -Recurse -Force; Write-Ok 'Settings and logs removed' }
    Write-Ok 'Done'
    return
}

$installed = Get-InstalledVersion
$work = New-WorkDir
try {
    if ($Package) {
        if (-not (Test-Path $Package)) { throw "File $Package not found" }
        $target = 'from ' + (Split-Path $Package -Leaf)
    } elseif ($Local) {
        $build = Invoke-LocalCompanionBuild $work
        $target = $build.Version
    } else {
        $tagName = Resolve-ReleaseTag -Repo $Repo -Channel $Channel -Tag $Tag
        $target = $tagName.TrimStart('v')
    }
    Write-Step "Companion: $target (installed: $(if ($installed) { $installed } else { 'none' }))"
    if ($CheckOnly) {
        if ($installed -eq $target) { Write-Ok 'The latest version is installed' } else { Write-Note 'An update is available' }
        return
    }
    if ($installed -eq $target -and -not $Local -and -not $Package) {
        Write-Ok "Companion $target is already installed"
        Update-Autostart
        if ($Firewall) { Add-FirewallRules }
        return
    }
    $setup = if ($Package) { (Resolve-Path $Package).Path } elseif ($Local) { $build.File } else { Get-ReleaseAsset $Repo $tagName '*-Setup.exe' $work }
    Install-Companion $setup $target
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
