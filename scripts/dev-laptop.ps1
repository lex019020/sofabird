<#
.SYNOPSIS
  Work with the TV laptop from the developer PC over SSH: install companion builds, view the log, check status.

.DESCRIPTION
  Once:
    1. .\dev-laptop.ps1 -Init                 # SSH key of this PC + a folder for the laptop (in the shared folder by default)
    2. on the laptop: \\<this PC>\shared\sofabird-setup\laptop-setup.cmd   (asks for admin rights)
    3. .\dev-laptop.ps1 -Connect <laptop IP or name> -User <user>   # what laptop-setup printed
  Then:
    .\dev-laptop.ps1 -Status                  # companion, VLC, network on the laptop
    .\dev-laptop.ps1 -Deploy -Local           # build the companion here and install it on the laptop
    .\dev-laptop.ps1 -Deploy [-Tag v0.2.0-beta0040]   # test build from GitHub Releases
    .\dev-laptop.ps1 -Logs [-Tail 200] [-Follow]
    .\dev-laptop.ps1 -Exec 'Get-Process vlc'  # any PowerShell command on the laptop
    .\dev-laptop.ps1 -Shell                   # interactive session

  Connection settings live in %USERPROFILE%\.ssh\config (Host sofabird-laptop), the key in %USERPROFILE%\.ssh\id_ed25519_sofabird.
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
    [string]$Repo = 'lex019020/sofabird'
)

. (Join-Path $PSScriptRoot 'deploy-lib.ps1')

$HostAlias = 'sofabird-laptop'
$SshDir    = Join-Path $env:USERPROFILE '.ssh'
$KeyFile   = Join-Path $SshDir 'id_ed25519_sofabird'
$SshConfig = Join-Path $SshDir 'config'

function Invoke-Remote([string]$Script, [switch]$Interactive) {
    # -EncodedCommand: no quoting issues, works with any default sshd shell.
    # A remote script error -> exit code 1 (otherwise ssh would return 0); -ExecutionPolicy Bypass applies only to this process.
    # *>&1: Write-Host/warnings come out as plain text (otherwise, without a console, PowerShell sends them to stderr as CLIXML).
    $wrapped = "`$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; `$ErrorActionPreference='Stop'; " +
        "& { try {`n" + $Script + "`n} catch { 'ERROR: ' + (`$_ | Out-String).Trim(); exit 1 } } *>&1 | ForEach-Object { `"`$_`" }"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
    $ErrorActionPreference = 'Continue'
    $sshArgs = @('-o', 'ConnectTimeout=8')
    if (-not $Interactive) { $sshArgs += @('-o', 'BatchMode=yes') }
    # Output goes straight to the screen: the function returns only the exit code.
    & ssh @sshArgs $HostAlias "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $b64" | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -eq 255) { throw "No SSH connection to the laptop ($HostAlias). Check that it is on and in the same network: .\dev-laptop.ps1 -Status" }
    return $code
}

function Protect-SshFile([string]$Path) {
    # OpenSSH refuses to read the config/key if other users have access to them (inherited permissions).
    if (-not (Test-Path $Path)) { return }
    $ErrorActionPreference = 'Continue'
    & icacls.exe $Path /inheritance:r /grant:r "${env:USERNAME}:F" /grant:r '*S-1-5-18:F' /grant:r '*S-1-5-32-544:F' | Out-Null
    $ErrorActionPreference = 'Stop'
}

function Assert-Configured {
    if (-not (Test-Path $SshConfig) -or -not (Select-String -Path $SshConfig -Pattern "^Host $HostAlias\b" -Quiet)) {
        throw 'The laptop is not set up yet: .\dev-laptop.ps1 -Init, then laptop-setup on the laptop and .\dev-laptop.ps1 -Connect <IP> -User <name>.'
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Init' {
        if (-not (Test-Path $SshDir)) { New-Item -ItemType Directory -Path $SshDir | Out-Null }
        if (-not (Test-Path $KeyFile)) {
            Write-Step 'Creating the SSH key of this PC'
            $ErrorActionPreference = 'Continue'
            & ssh-keygen -q -t ed25519 -N '""' -C 'sofabird-dev' -f $KeyFile
            $ErrorActionPreference = 'Stop'
            if (-not (Test-Path "$KeyFile.pub")) { throw 'ssh-keygen did not create the key' }
        }
        Protect-SshFile $KeyFile
        if (-not $SetupDir) {
            $share = Get-SmbShare -Name shared -ErrorAction SilentlyContinue
            $SetupDir = if ($share) { Join-Path $share.Path 'sofabird-setup' } else { Join-Path $env:USERPROFILE 'Desktop\sofabird-setup' }
        }
        New-Item -ItemType Directory -Path $SetupDir -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'laptop-setup.ps1'), (Join-Path $PSScriptRoot 'laptop-setup.cmd') $SetupDir -Force
        Copy-Item "$KeyFile.pub" (Join-Path $SetupDir 'sofabird_dev.pub') -Force
        Write-Ok "Files for the laptop: $SetupDir"
        if ($SetupDir -like '*\sofabird-setup' -and (Get-SmbShare -Name shared -ErrorAction SilentlyContinue)) {
            Write-Ok "On the laptop, open: \\$env:COMPUTERNAME\shared\sofabird-setup\laptop-setup.cmd"
        } else {
            Write-Note 'Move this folder to the laptop and run laptop-setup.cmd.'
        }
        Write-Note 'Then: .\dev-laptop.ps1 -Connect <laptop IP or name> -User <user> (laptop-setup prints them)'
    }
    'Connect' {
        if (-not $User) { throw 'Specify -User: the user name on the laptop (laptop-setup printed it)' }
        if (-not (Test-Path $KeyFile)) { throw 'Run .\dev-laptop.ps1 -Init first' }
        $block = "Host $HostAlias`r`n    HostName $Connect`r`n    User $User`r`n    IdentityFile $KeyFile`r`n    IdentitiesOnly yes`r`n    StrictHostKeyChecking accept-new`r`n    ServerAliveInterval 15`r`n"
        $text = if (Test-Path $SshConfig) { Get-Content $SshConfig -Raw } else { '' }
        $pattern = "(?ms)^Host $HostAlias\b.*?(?=^Host |\z)"
        $text = if ($text -match $pattern) { [regex]::Replace($text, $pattern, $block) } else { ($text.TrimEnd() + "`r`n`r`n" + $block).TrimStart() }
        [IO.File]::WriteAllText($SshConfig, $text, (New-Object Text.UTF8Encoding($false)))
        Protect-SshFile $SshConfig
        Protect-SshFile $KeyFile
        Write-Ok "Written to $SshConfig (Host $HostAlias)"
        $null = Invoke-Remote 'Write-Output ("Connected: " + $env:COMPUTERNAME + " / " + $env:USERNAME)'
    }
    'Status' {
        Assert-Configured
        $null = Invoke-Remote @'
$exe = Join-Path $env:LOCALAPPDATA 'Sofabird\current\Sofabird.exe'
$ver = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { $null }
"Laptop:    $env:COMPUTERNAME ($((Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias)=$($_.NetworkCategory)" }) -join ', '))"
"Companion: $(if ($ver) { $ver } else { 'not installed' }); process: $(if (Get-Process Sofabird -ErrorAction SilentlyContinue) { 'running' } else { 'none' })"
try { $i = Invoke-RestMethod http://127.0.0.1:47555/info -TimeoutSec 3; "  /info:    $($i | ConvertTo-Json -Compress -Depth 4)" } catch { "  /info:    no response ($($_.Exception.Message))" }
$v = Get-CimInstance Win32_Process -Filter "Name='vlc.exe'"
"VLC:       $(if ($v) { ($v | ForEach-Object { "pid $($_.ProcessId): $($_.CommandLine)" }) -join '; ' } else { 'not running' })"
$rc = Join-Path $env:APPDATA 'vlc\vlcrc'
if (Test-Path $rc) { "vlcrc:     " + ((Select-String -Path $rc -Pattern '^(extraintf|http-port|http-password|rc-host|rc-quiet)=' | ForEach-Object { $_.Line -replace '(http-password=).+', '$1***' }) -join '; ') }
'@
    }
    'Deploy' {
        Assert-Configured
        $work = New-WorkDir
        try {
            if ($Local) {
                $build = Invoke-LocalCompanionBuild $work
                $setup = $build.File
            } else {
                $tagName = Resolve-ReleaseTag -Repo $Repo -Channel $Channel -Tag $Tag
                $setup = Get-ReleaseAsset $Repo $tagName '*-Setup.exe' $work
            }
            Write-Step "Copying to the laptop: $(Split-Path $setup -Leaf)"
            $null = Invoke-Remote 'New-Item -ItemType Directory -Force -Path (Join-Path $HOME "sofabird-deploy") | Out-Null'
            $files = @($setup, (Join-Path $PSScriptRoot 'deploy-lib.ps1'), (Join-Path $PSScriptRoot 'deploy-windows.ps1'))
            $ErrorActionPreference = 'Continue'
            & scp -q -o BatchMode=yes @files "${HostAlias}:sofabird-deploy/"
            $scpCode = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($scpCode -ne 0) { throw "scp exited with code $scpCode" }
            $leaf = Split-Path $setup -Leaf
            $code = Invoke-Remote "& (Join-Path `$HOME 'sofabird-deploy\deploy-windows.ps1') -Package (Join-Path `$HOME 'sofabird-deploy\$leaf')"
            if ($code -ne 0) { throw "Installation on the laptop exited with code $code" }
        } finally {
            Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    'Logs' {
        Assert-Configured
        $wait = if ($Follow) { ' -Wait' } else { '' }
        $null = Invoke-Remote "`$f = Get-ChildItem (Join-Path `$env:APPDATA 'Sofabird\logs') -Filter 'companion-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1; if (-not `$f) { 'No logs' } else { Get-Content `$f.FullName -Tail $Tail -Encoding UTF8$wait }"
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
