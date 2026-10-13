<#
.SYNOPSIS
  Работа с ноутбуком у ТВ с ПК разработчика по SSH: установка сборок компаньона, журнал, статус.

.DESCRIPTION
  Один раз:
    1. .\dev-laptop.ps1 -Init                 # ключ SSH этого ПК + папка для ноутбука (по умолчанию в общей папке)
    2. на ноутбуке: \\<этот ПК>\shared\vlc-remote-setup\laptop-setup.cmd   (запросит права администратора)
    3. .\dev-laptop.ps1 -Connect <IP или имя ноутбука> -User <пользователь>   # то, что напечатал laptop-setup
  Дальше:
    .\dev-laptop.ps1 -Status                  # компаньон, VLC, сеть на ноутбуке
    .\dev-laptop.ps1 -Deploy -Local           # собрать компаньон здесь и поставить на ноутбук
    .\dev-laptop.ps1 -Deploy [-Tag v0.2.0-beta.3]   # тестовая сборка из GitHub (gh нужен только здесь)
    .\dev-laptop.ps1 -Logs [-Tail 200] [-Follow]
    .\dev-laptop.ps1 -Exec 'Get-Process vlc'  # любая команда PowerShell на ноутбуке
    .\dev-laptop.ps1 -Shell                   # интерактивная сессия

  Настройки подключения — в %USERPROFILE%\.ssh\config (Host vlcremote-laptop), ключ — %USERPROFILE%\.ssh\id_ed25519_vlcremote.
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Init')][switch]$Init,
    [Parameter(ParameterSetName = 'Init')][string]$SetupDir,
    [Parameter(ParameterSetName = 'Connect')][string]$Connect,
    [Parameter(ParameterSetName = 'Connect')][string]$User,
    [Parameter(ParameterSetName = 'Status')][switch]$Status,
    [Parameter(ParameterSetName = 'Deploy')][switch]$Deploy,
    [Parameter(ParameterSetName = 'Deploy')][switch]$Local,
    [Parameter(ParameterSetName = 'Deploy')][string]$Tag,
    [Parameter(ParameterSetName = 'Deploy')][ValidateSet('beta', 'stable')][string]$Channel = 'beta',
    [Parameter(ParameterSetName = 'Logs')][switch]$Logs,
    [Parameter(ParameterSetName = 'Logs')][int]$Tail = 150,
    [Parameter(ParameterSetName = 'Logs')][switch]$Follow,
    [Parameter(ParameterSetName = 'Exec')][string]$Exec,
    [Parameter(ParameterSetName = 'Shell')][switch]$Shell,
    [string]$Repo = 'lex019020/vlc-remote'
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$HostAlias = 'vlcremote-laptop'
$SshDir    = Join-Path $env:USERPROFILE '.ssh'
$KeyFile   = Join-Path $SshDir 'id_ed25519_vlcremote'
$SshConfig = Join-Path $SshDir 'config'

function Invoke-Remote([string]$Script, [switch]$Interactive) {
    # -EncodedCommand: без проблем с кавычками, работает при любой оболочке sshd по умолчанию.
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + $Script))
    $ErrorActionPreference = 'Continue'
    $sshArgs = @('-o', 'ConnectTimeout=8')
    if (-not $Interactive) { $sshArgs += @('-o', 'BatchMode=yes') }
    & ssh @sshArgs $HostAlias "powershell -NoProfile -NonInteractive -EncodedCommand $b64"
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -eq 255) { throw "Нет SSH-связи с ноутбуком ($HostAlias). Проверьте, что он включён и в той же сети: .\dev-laptop.ps1 -Status" }
    return $code
}

function Assert-Configured {
    if (-not (Test-Path $SshConfig) -or -not (Select-String -Path $SshConfig -Pattern "^Host $HostAlias\b" -Quiet)) {
        throw 'Ноутбук ещё не настроен: .\dev-laptop.ps1 -Init, затем laptop-setup на ноутбуке и .\dev-laptop.ps1 -Connect <IP> -User <имя>.'
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Init' {
        if (-not (Test-Path $SshDir)) { New-Item -ItemType Directory -Path $SshDir | Out-Null }
        if (-not (Test-Path $KeyFile)) {
            Write-Step 'Создаю SSH-ключ этого ПК'
            $ErrorActionPreference = 'Continue'
            & ssh-keygen -q -t ed25519 -N '""' -C 'vlcremote-dev' -f $KeyFile
            $ErrorActionPreference = 'Stop'
            if (-not (Test-Path "$KeyFile.pub")) { throw 'ssh-keygen не создал ключ' }
        }
        if (-not $SetupDir) {
            $share = Get-SmbShare -Name shared -ErrorAction SilentlyContinue
            $SetupDir = if ($share) { Join-Path $share.Path 'vlc-remote-setup' } else { Join-Path $env:USERPROFILE 'Desktop\vlc-remote-setup' }
        }
        New-Item -ItemType Directory -Path $SetupDir -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'laptop-setup.ps1'), (Join-Path $PSScriptRoot 'laptop-setup.cmd') $SetupDir -Force
        Copy-Item "$KeyFile.pub" (Join-Path $SetupDir 'vlcremote_dev.pub') -Force
        Write-Ok "Файлы для ноутбука: $SetupDir"
        if ($SetupDir -like '*\vlc-remote-setup' -and (Get-SmbShare -Name shared -ErrorAction SilentlyContinue)) {
            Write-Ok "На ноутбуке откройте: \\$env:COMPUTERNAME\shared\vlc-remote-setup\laptop-setup.cmd"
        } else {
            Write-Note 'Перенесите эту папку на ноутбук и запустите laptop-setup.cmd.'
        }
        Write-Note 'Затем: .\dev-laptop.ps1 -Connect <IP или имя ноутбука> -User <пользователь> (их напечатает laptop-setup)'
    }
    'Connect' {
        if (-not $User) { throw 'Укажите -User — имя пользователя на ноутбуке (его напечатал laptop-setup)' }
        if (-not (Test-Path $KeyFile)) { throw 'Сначала .\dev-laptop.ps1 -Init' }
        $block = "Host $HostAlias`r`n    HostName $Connect`r`n    User $User`r`n    IdentityFile $KeyFile`r`n    IdentitiesOnly yes`r`n    StrictHostKeyChecking accept-new`r`n    ServerAliveInterval 15`r`n"
        $text = if (Test-Path $SshConfig) { Get-Content $SshConfig -Raw } else { '' }
        $pattern = "(?ms)^Host $HostAlias\b.*?(?=^Host |\z)"
        $text = if ($text -match $pattern) { [regex]::Replace($text, $pattern, $block) } else { ($text.TrimEnd() + "`r`n`r`n" + $block).TrimStart() }
        [IO.File]::WriteAllText($SshConfig, $text, (New-Object Text.UTF8Encoding($false)))
        Write-Ok "Записано в $SshConfig (Host $HostAlias)"
        $null = Invoke-Remote 'Write-Output ("Связь есть: " + $env:COMPUTERNAME + " / " + $env:USERNAME)'
    }
    'Status' {
        Assert-Configured
        $null = Invoke-Remote @'
$root = Join-Path $env:LOCALAPPDATA 'VlcRemote'
$ver = Get-Content (Join-Path $root 'app\version.txt') -ErrorAction SilentlyContinue
"Ноутбук:   $env:COMPUTERNAME ($((Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias)=$($_.NetworkCategory)" }) -join ', '))"
"Компаньон: $(if ($ver) { $ver } else { 'не установлен' }); процесс: $(if (Get-Process VlcRemote.Companion -ErrorAction SilentlyContinue) { 'запущен' } else { 'нет' })"
try { $i = Invoke-RestMethod http://127.0.0.1:47555/info -TimeoutSec 3; "  /info:    $($i | ConvertTo-Json -Compress -Depth 4)" } catch { "  /info:    нет ответа ($($_.Exception.Message))" }
$v = Get-CimInstance Win32_Process -Filter "Name='vlc.exe'"
"VLC:       $(if ($v) { ($v | ForEach-Object { "pid $($_.ProcessId): $($_.CommandLine)" }) -join '; ' } else { 'не запущен' })"
$rc = Join-Path $env:APPDATA 'vlc\vlcrc'
if (Test-Path $rc) { "vlcrc:     " + ((Select-String -Path $rc -Pattern '^(extraintf|http-port|http-password|rc-host|rc-quiet)=' | ForEach-Object { $_.Line -replace '(http-password=).+', '$1***' }) -join '; ') }
'@
    }
    'Deploy' {
        Assert-Configured
        $work = New-WorkDir
        try {
            if ($Local) {
                $build = Build-LocalCompanion $work
                $zip = $build.File
            } else {
                Assert-Gh
                $tagName = Resolve-ReleaseTag
                $zip = Get-ReleaseAsset $tagName 'VlcRemote.Companion-*-win-x64.zip' $work
            }
            Write-Step "Копирую на ноутбук: $(Split-Path $zip -Leaf)"
            $null = Invoke-Remote 'New-Item -ItemType Directory -Force -Path (Join-Path $HOME "vlcremote-deploy") | Out-Null'
            $files = @($zip, (Join-Path $PSScriptRoot 'deploy-lib.ps1'), (Join-Path $PSScriptRoot 'deploy-windows.ps1'))
            $ErrorActionPreference = 'Continue'
            & scp -q -o BatchMode=yes @files "${HostAlias}:vlcremote-deploy/"
            $scpCode = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($scpCode -ne 0) { throw "scp завершился с кодом $scpCode" }
            $leaf = Split-Path $zip -Leaf
            $code = Invoke-Remote "& (Join-Path `$HOME 'vlcremote-deploy\deploy-windows.ps1') -Package (Join-Path `$HOME 'vlcremote-deploy\$leaf'); exit `$LASTEXITCODE"
            if ($code -ne 0) { throw "Установка на ноутбуке завершилась с кодом $code" }
        } finally {
            Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    'Logs' {
        Assert-Configured
        $wait = if ($Follow) { ' -Wait' } else { '' }
        $null = Invoke-Remote "`$f = Get-ChildItem (Join-Path `$env:LOCALAPPDATA 'VlcRemote\logs') -Filter 'companion-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1; if (-not `$f) { 'Журналов нет' } else { Get-Content `$f.FullName -Tail $Tail -Encoding UTF8$wait }"
    }
    'Exec' {
        Assert-Configured
        $code = Invoke-Remote $Exec
        exit $code
    }
    'Shell' {
        Assert-Configured
        & ssh $HostAlias
    }
}
