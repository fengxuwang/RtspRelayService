#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\RtspRelayService"
)

$ErrorActionPreference = "Stop"
$serviceName = "RtspRelayService"
$exePath = Join-Path $InstallDirectory "RtspRelayService.exe"
$configPath = Join-Path $InstallDirectory "appsettings.json"
$ffmpegPath = Join-Path $InstallDirectory "ffmpeg\ffmpeg.exe"

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Executable not found: $exePath"
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Configuration not found: $configPath"
}
if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) {
    throw "FFmpeg executable not found: $ffmpegPath"
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service $serviceName is already installed."
}

$configAcl = New-Object System.Security.AccessControl.FileSecurity
$configAcl.SetAccessRuleProtection($true, $false)
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$administrators = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
$system = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18")
$administratorsRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $administrators,
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    $allow)
$systemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $system,
    [System.Security.AccessControl.FileSystemRights]::Read,
    $allow)
$configAcl.AddAccessRule($administratorsRule)
$configAcl.AddAccessRule($systemRule)
Set-Acl -LiteralPath $configPath -AclObject $configAcl

# Windows service binary paths must include literal quotes when the path contains spaces.
$binaryPath = '"{0}"' -f $exePath
$serviceParameters = @{
    Name = $serviceName
    BinaryPathName = $binaryPath
    DisplayName = "RTSP Relay Service"
    Description = "Relays RTSP cameras to ZLMediaKit over RTMP."
    StartupType = "Automatic"
}

New-Service @serviceParameters | Out-Null

& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/60000
if ($LASTEXITCODE -ne 0) {
    throw "Failed to configure service recovery actions (sc.exe exit code $LASTEXITCODE)."
}

& sc.exe failureflag $serviceName 1
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enable service recovery for non-crash failures (sc.exe exit code $LASTEXITCODE)."
}

Start-Service -Name $serviceName
Get-Service -Name $serviceName
