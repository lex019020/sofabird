<#
.SYNOPSIS
  One-time setup of the TV laptop for remote debugging from the developer PC (OpenSSH Server).

.DESCRIPTION
  Run ON THE LAPTOP as administrator (laptop-setup.cmd asks for the rights itself).
  * installs the OpenSSH Server built into Windows and enables automatic start of the sshd service;
  * allows incoming SSH (port 22) in the private network only;
  * makes PowerShell the default SSH shell;
  * adds the developer PC's public key (the vlcremote_dev.pub file next to the script, or -PublicKey);
  * prints the laptop name, the user and the IP - pass them to the developer PC (dev-laptop.ps1 -Init).
  The user's password is not used over the network: key-only login.

.EXAMPLE
  .\laptop-setup.ps1
  .\laptop-setup.ps1 -PublicKey 'ssh-ed25519 AAAA... vlcremote-dev'
  .\laptop-setup.ps1 -Remove          # turn off sshd and remove the developer key
#>
[CmdletBinding()]
param(
    [string]$PublicKey,
    [string]$PublicKeyFile,
    [string]$UserName = $env:USERNAME,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot can be empty in parameter default values (PS 5.1, run from a network/mapped drive).
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $PublicKeyFile) { $PublicKeyFile = Join-Path $scriptDir 'vlcremote_dev.pub' }
function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required: run laptop-setup.cmd (it asks for them itself) or PowerShell "as administrator".'
}

$isAdminUser = [bool](Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "\\$([regex]::Escape($UserName))$" })
if (-not $isAdminUser) {
    # Localized Windows (e.g. Russian): the Administrators group has a translated name, so look it up by its SID
    $isAdminUser = [bool](Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\\$([regex]::Escape($UserName))$" })
}
$keysFile = if ($isAdminUser) { Join-Path $env:ProgramData 'ssh\administrators_authorized_keys' }
            else { Join-Path "C:\Users\$UserName" '.ssh\authorized_keys' }

if ($Remove) {
    Write-Step 'Disabling SSH access'
    if (Test-Path $keysFile) {
        $lines = Get-Content $keysFile | Where-Object { $_ -notmatch 'vlcremote-dev' }
        Set-Content -Path $keysFile -Value $lines -Encoding ascii
        Write-Ok "Developer key removed from $keysFile"
    }
    Stop-Service sshd -ErrorAction SilentlyContinue
    Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue
    Write-Ok 'The sshd service is stopped and disabled'
    return
}

if (-not $PublicKey) {
    if (-not (Test-Path $PublicKeyFile)) { throw "No public key: put vlcremote_dev.pub next to the script or pass -PublicKey." }
    $PublicKey = (Get-Content $PublicKeyFile -TotalCount 1).Trim()
}
if ($PublicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+) \S+') { throw 'The key string does not look like an OpenSSH public key.' }

Write-Step 'OpenSSH Server'
$cap = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') {
    Write-Note 'Installing (may take a couple of minutes)...'
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd
Write-Ok 'sshd is running, automatic start enabled'

Write-Step 'Firewall: SSH in the private network only'
$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if ($rule) { Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Profile Private -Enabled True }
else { New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Private | Out-Null }
Write-Ok 'Port 22 is open for the private network'

Write-Step 'Default shell: PowerShell'
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null
Write-Ok 'OK'

Write-Step "Developer key -> $keysFile"
$dir = Split-Path $keysFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$existing = @(); if (Test-Path $keysFile) { $existing = @(Get-Content $keysFile) }
$keyBody = ($PublicKey -split ' ')[1]
if ($existing | Where-Object { $_ -like "*$keyBody*" }) { Write-Ok 'Key already added' }
else {
    Add-Content -Path $keysFile -Value $PublicKey -Encoding ascii
    Write-Ok 'Key added'
}
if ($isAdminUser) {
    # sshd accepts administrators_authorized_keys only with Administrators + SYSTEM permissions
    & icacls.exe $keysFile /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
} else {
    & icacls.exe $keysFile /inheritance:r /grant "${UserName}:F" /grant '*S-1-5-18:F' | Out-Null
}

$ips = Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp, Manual -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' } | Select-Object -ExpandProperty IPAddress
$profiles = Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias): $($_.NetworkCategory)" }
Write-Step 'Done. Pass this to the developer PC:'
Write-Host "    Name:         $env:COMPUTERNAME"
Write-Host "    User:         $UserName"
Write-Host "    IP:           $($ips -join ', ')"
Write-Host "    Networks:     $($profiles -join '; ')"
if ($profiles -match 'Public') { Write-Note 'A network has the "Public" profile: SSH (and the companion) are closed in it. Switch your home network to "Private".' }
