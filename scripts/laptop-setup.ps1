<#
.SYNOPSIS
  Разовая настройка ноутбука у ТВ для удалённой отладки с ПК разработчика (OpenSSH Server).

.DESCRIPTION
  Запускать НА НОУТБУКЕ от имени администратора (laptop-setup.cmd сам запросит права).
  * ставит встроенный в Windows OpenSSH Server, включает автозапуск службы sshd;
  * разрешает входящий SSH (порт 22) только в частной сети;
  * делает PowerShell оболочкой по умолчанию для SSH;
  * добавляет открытый ключ ПК разработчика (файл vlcremote_dev.pub рядом со скриптом или -PublicKey);
  * печатает имя ноутбука, пользователя и IP — их нужно передать на ПК разработчика (dev-laptop.ps1 -Init).
  Пароль пользователя по сети не используется: вход только по ключу.

.EXAMPLE
  .\laptop-setup.ps1
  .\laptop-setup.ps1 -PublicKey 'ssh-ed25519 AAAA... vlcremote-dev'
  .\laptop-setup.ps1 -Remove          # выключить sshd и убрать ключ разработчика
#>
[CmdletBinding()]
param(
    [string]$PublicKey,
    [string]$PublicKeyFile = (Join-Path $PSScriptRoot 'vlcremote_dev.pub'),
    [string]$UserName = $env:USERNAME,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Нужны права администратора: запустите laptop-setup.cmd (он сам запросит их) или PowerShell «от имени администратора».'
}

$isAdminUser = [bool](Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "\\$([regex]::Escape($UserName))$" })
if (-not $isAdminUser) {
    # Русская Windows: группа «Администраторы»
    $isAdminUser = [bool](Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\\$([regex]::Escape($UserName))$" })
}
$keysFile = if ($isAdminUser) { Join-Path $env:ProgramData 'ssh\administrators_authorized_keys' }
            else { Join-Path "C:\Users\$UserName" '.ssh\authorized_keys' }

if ($Remove) {
    Write-Step 'Отключение SSH-доступа'
    if (Test-Path $keysFile) {
        $lines = Get-Content $keysFile | Where-Object { $_ -notmatch 'vlcremote-dev' }
        Set-Content -Path $keysFile -Value $lines -Encoding ascii
        Write-Ok "Ключ разработчика удалён из $keysFile"
    }
    Stop-Service sshd -ErrorAction SilentlyContinue
    Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue
    Write-Ok 'Служба sshd остановлена и отключена'
    return
}

if (-not $PublicKey) {
    if (-not (Test-Path $PublicKeyFile)) { throw "Нет открытого ключа: положите vlcremote_dev.pub рядом со скриптом или передайте -PublicKey." }
    $PublicKey = (Get-Content $PublicKeyFile -TotalCount 1).Trim()
}
if ($PublicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+) \S+') { throw 'Строка ключа не похожа на открытый ключ OpenSSH.' }

Write-Step 'OpenSSH Server'
$cap = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') {
    Write-Note 'Устанавливаю (может занять пару минут)...'
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
}
Set-Service sshd -StartupType Automatic
Start-Service sshd
Write-Ok 'sshd запущен, автозапуск включён'

Write-Step 'Брандмауэр: SSH только в частной сети'
$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if ($rule) { Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Profile Private -Enabled True }
else { New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Private | Out-Null }
Write-Ok 'Порт 22 открыт для частной сети'

Write-Step 'Оболочка по умолчанию: PowerShell'
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null
Write-Ok 'OK'

Write-Step "Ключ разработчика -> $keysFile"
$dir = Split-Path $keysFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$existing = @(); if (Test-Path $keysFile) { $existing = @(Get-Content $keysFile) }
$keyBody = ($PublicKey -split ' ')[1]
if ($existing | Where-Object { $_ -like "*$keyBody*" }) { Write-Ok 'Ключ уже добавлен' }
else {
    Add-Content -Path $keysFile -Value $PublicKey -Encoding ascii
    Write-Ok 'Ключ добавлен'
}
if ($isAdminUser) {
    # sshd принимает administrators_authorized_keys только с правами Administrators + SYSTEM
    & icacls.exe $keysFile /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
} else {
    & icacls.exe $keysFile /inheritance:r /grant "${UserName}:F" /grant '*S-1-5-18:F' | Out-Null
}

$ips = Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp, Manual -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' } | Select-Object -ExpandProperty IPAddress
$profiles = Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias): $($_.NetworkCategory)" }
Write-Step 'Готово. Передайте на ПК разработчика:'
Write-Host "    Имя:          $env:COMPUTERNAME"
Write-Host "    Пользователь: $UserName"
Write-Host "    IP:           $($ips -join ', ')"
Write-Host "    Сети:         $($profiles -join '; ')"
if ($profiles -match 'Public') { Write-Note 'Есть сеть с профилем «Общедоступная» — SSH (и компаньон) в ней закрыты. Переключите домашнюю сеть на «Частная».' }
