# Installs (or updates) the Arcade PC agent for the signed-in Windows user.
#
# On the PC itself, from an elevated PowerShell in that user's session:
#   .\install-arcade-pc-agent.ps1 -ExePath .\ArcadePcAgent.exe
# then open the tray icon's Settings to enter the broker (it opens by itself
# when there are no settings yet).
#
# Scripted, e.g. from ArcadePcAgent-Setup.exe or a deployment tool:
#   .\install-arcade-pc-agent.ps1 -ExePath .\ArcadePcAgent.exe -SettingsJson agent.json `
#       [-CaPem ca.pem] [-PasswordFile pw.txt]
#
# From macOS/Linux, scripts/install-arcade-pc-agent.sh builds the agent, stages
# the exe, agent.json, an optional CA file and the password on the PC over SSH,
# and runs this script with -ExpectedSha256 and -FromStaging.
#
# Safe to rerun; everything is read back and any mismatch throws.
#
# Layout:
#   C:\Program Files\ArcadePcAgent\ArcadePcAgent.exe   (admin-writable only)
#   %LOCALAPPDATA%\ArcadePcAgent\agent.json            settings
#   %LOCALAPPDATA%\ArcadePcAgent\agent.password        MQTT password, DPAPI (this user)
#   %LOCALAPPDATA%\ArcadePcAgent\ca.pem                optional private CA
#   %LOCALAPPDATA%\ArcadePcAgent\agent.log             log
#   scheduled task "Arcade PC agent"                    at logon, this user, elevated, restarts on failure
param(
    # The agent exe to install. Default: the staged copy (-FromStaging).
    [string]$ExePath,
    # Check the exe against this SHA256 (the remote installer always passes it).
    [string]$ExpectedSha256,
    # Settings to install (agent.json format); without it, existing settings stay.
    [string]$SettingsJson,
    # PEM of a private CA to install as ca.pem and point caFile at.
    [string]$CaPem,
    # File holding the MQTT password in plain text; it is DPAPI-protected for
    # this user and then DELETED.
    [string]$PasswordFile,
    # Take the exe, agent.json, ca.pem and the password from the staging folder
    # (scripts/install-arcade-pc-agent.sh puts them there over SSH).
    [switch]$FromStaging,
    # Don't wait for the agent to log 'Connected' (e.g. no settings yet).
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'

$InstallDir = 'C:\Program Files\ArcadePcAgent'
$Exe = Join-Path $InstallDir 'ArcadePcAgent.exe'
$DataDir = Join-Path $env:LOCALAPPDATA 'ArcadePcAgent'
$Staging = 'C:\ProgramData\ArcadePcAgent\staging'
$TaskName = 'Arcade PC agent'
$SettingsFile = Join-Path $DataDir 'agent.json'
$CaFile = Join-Path $DataDir 'ca.pem'
$StoredPassword = Join-Path $DataDir 'agent.password'
$PasswordNew = Join-Path $DataDir 'agent.password.new'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this from an elevated PowerShell (the agent runs elevated so it can close an elevated BigBox)' }
$console = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $console) { throw 'Nobody is signed in at the console' }
if ($console -ne (whoami)) { throw "Run this as the signed-in user $console (running as $(whoami)); the settings and password belong to that user" }

if (-not $ExePath) {
    if (-not $FromStaging) { throw 'Pass -ExePath <ArcadePcAgent.exe>' }
    $ExePath = Join-Path $Staging 'ArcadePcAgent.exe'
}
if (-not (Test-Path $ExePath)) { throw "Agent exe not found: $ExePath" }
$sha = (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedSha256 -and $sha -ne $ExpectedSha256.ToLowerInvariant()) { throw "$ExePath has SHA256 $sha, expected $ExpectedSha256" }

function Stop-Agent {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Get-Process ArcadePcAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 20 -and (Get-Process ArcadePcAgent -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-Process ArcadePcAgent -ErrorAction SilentlyContinue) { throw 'ArcadePcAgent did not stop' }
}

# --- binary -----------------------------------------------------------------
$restart = $false
if (-not (Test-Path $Exe) -or (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
    Stop-Agent
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item $ExePath $Exe -Force
    if ((Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) { throw "$Exe did not copy correctly" }
    $restart = $true
}
Write-Output "Verified: $Exe ($sha)"

# --- settings, CA, password (when given) ---------------------------------
New-Item -ItemType Directory -Force $DataDir | Out-Null
if ($FromStaging) {
    if (-not $SettingsJson) { $SettingsJson = Join-Path $Staging 'agent.json' }
    if (-not $CaPem -and (Test-Path (Join-Path $Staging 'ca.pem'))) { $CaPem = Join-Path $Staging 'ca.pem' }
    if (-not $PasswordFile -and (Test-Path $PasswordNew)) { $PasswordFile = $PasswordNew }
}
if ($SettingsJson) {
    if (-not (Test-Path $SettingsJson)) { throw "Settings file not found: $SettingsJson" }
    $json = Get-Content $SettingsJson -Raw | ConvertFrom-Json
    if ($CaPem) {
        if (-not (Test-Path $CaPem)) { throw "CA file not found: $CaPem" }
        $caText = (Get-Content $CaPem -Raw).Trim()
        $null = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Text.Encoding]::ASCII.GetBytes($caText)) # throws if not a certificate
        if (-not (Test-Path $CaFile) -or (Get-Content $CaFile -Raw).Trim() -ne $caText) {
            Set-Content -Path $CaFile -Value $caText -Encoding ascii
            $restart = $true
        }
        $json | Add-Member -NotePropertyName caFile -NotePropertyValue $CaFile -Force
    }
    $body = ($json | ConvertTo-Json -Depth 3).Trim()
    $current = if (Test-Path $SettingsFile) { (Get-Content $SettingsFile -Raw).Trim() } else { '' }
    if ($current -ne $body) {
        Set-Content -Path $SettingsFile -Value $body -Encoding utf8
        $restart = $true
    }
    if ((Get-Content $SettingsFile -Raw).Trim() -ne $body) { throw "$SettingsFile did not read back" }
}
if ($PasswordFile) {
    if (-not (Test-Path $PasswordFile)) { throw "Password file not found: $PasswordFile" }
    Add-Type -AssemblyName System.Security
    $plain = (Get-Content $PasswordFile -Raw).Trim()
    try {
        $current = if (Test-Path $StoredPassword) {
            [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content $StoredPassword -Raw).Trim()), $null, 'CurrentUser'))
        } else { $null }
        if ($current -ne $plain) {
            $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($plain), $null, 'CurrentUser')
            Set-Content -Path $StoredPassword -Value ([Convert]::ToBase64String($protected)) -Encoding ascii
            $restart = $true
        }
    } finally {
        # The plain-text file is always deleted, whatever happened.
        Remove-Item $PasswordFile -Force
    }
    if (Test-Path $PasswordFile) { throw "Could not remove $PasswordFile" }
    $check = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content $StoredPassword -Raw).Trim()), $null, 'CurrentUser'))
    if ($check -ne $plain) { throw "$StoredPassword does not decrypt to the new password" }
}
if ($FromStaging) { Remove-Item $Staging -Recurse -Force -ErrorAction SilentlyContinue }
if ($SettingsJson -or $PasswordFile) {
    Write-Output "Verified: $(@($(if (Test-Path $SettingsFile) { 'agent.json' }), $(if (Test-Path $CaFile) { 'ca.pem' }), $(if (Test-Path $StoredPassword) { 'DPAPI password' })) -ne $null -join ', ') in $DataDir"
}
$configured = Test-Path $SettingsFile

# --- scheduled task -------------------------------------------------------
$action = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $console
# Elevated: LaunchBox/BigBox usually run elevated, and a non-elevated agent can
# neither watch them nor send them a close (UIPI).
$principal = New-ScheduledTaskPrincipal -UserId $console -LogonType Interactive -RunLevel Highest
# Restarts after a crash; a Quit from the tray exits cleanly and stays stopped until next sign-in.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
$old = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $old -or $old.Principal.RunLevel -ne 'Highest') { $restart = $true }
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
$t = Get-ScheduledTask -TaskName $TaskName
if ($t.Actions[0].Execute -ne $Exe -or $t.Triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger' -or $t.Settings.ExecutionTimeLimit -ne 'PT0S' -or $t.Principal.RunLevel -ne 'Highest') {
    throw "'$TaskName' read back wrong: $($t.Actions[0].Execute) / $($t.Triggers[0].CimClass.CimClassName) / $($t.Settings.ExecutionTimeLimit) / $($t.Principal.RunLevel)"
}
Write-Output "Verified: '$TaskName' runs $Exe at sign-in as $console, elevated (no time limit, restarts on failure)"

# --- (re)start and prove it connected --------------------------------------
if ($restart -or -not (Get-Process ArcadePcAgent -ErrorAction SilentlyContinue)) {
    Stop-Agent
    $since = Get-Date
    Start-ScheduledTask -TaskName $TaskName
    $log = Join-Path $DataDir 'agent.log'
    if ($configured -and -not $NoWait) {
        $connected = $false
        for ($i = 0; $i -lt 40 -and -not $connected; $i++) {
            Start-Sleep -Milliseconds 500
            if (Test-Path $log) {
                $connected = [bool](Get-Content $log -Tail 50 | Where-Object {
                        $_ -match '^(\S+ \S+)' -and [datetime]::Parse($Matches[1]) -ge $since.AddSeconds(-1) -and $_ -like '*INFO Connected to *'
                    })
            }
        }
        if (-not $connected) { throw "Agent did not log 'Connected' within 20 s. Log tail:`n$((Get-Content $log -Tail 15 -ErrorAction SilentlyContinue) -join "`n")" }
    } else {
        Start-Sleep -Seconds 3
    }
}
$proc = Get-Process ArcadePcAgent -ErrorAction SilentlyContinue
if (-not $proc) { throw 'ArcadePcAgent is not running' }
if ($proc.SessionId -eq 0) { throw 'ArcadePcAgent is running in session 0 (no desktop, no audio)' }
if ($configured -and -not $NoWait) {
    Write-Output "Verified: ArcadePcAgent running in session $($proc.SessionId) (pid $($proc.Id)) and connected"
} else {
    Write-Output "ArcadePcAgent is running in session $($proc.SessionId) (pid $($proc.Id)). Open its tray icon > Settings to configure it."
}
