#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\ActivityRecordingService",
    [AllowNull()]
    [PSCredential]$ServiceAccount
)

$ErrorActionPreference = "Stop"

function Get-ConfigurationValue {
    param(
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)][string]$Name,
        [string]$DefaultValue = ""
    )

    $property = $Configuration.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $DefaultValue
    }

    return [string]$property.Value
}

function Get-ConfigurationBoolean {
    param(
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Configuration.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $false
    }

    if ($null -eq $property.Value) {
        throw "ActivityRecording:$Name must be JSON true or false; JSON null is not accepted."
    }

    if ($property.Value -is [bool]) {
        return $property.Value
    }

    throw "ActivityRecording:$Name must be a JSON boolean. Quote-free true and false are required; strings such as `"false`" are not accepted."
}

function Join-InstallPath {
    param(
        [Parameter(Mandatory)][string]$BaseDirectory,
        [Parameter(Mandatory)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path -Path $BaseDirectory -ChildPath $Path))
}

function Get-NormalizedStoragePath {
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Assert-DedicatedPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $volumeRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ($fullPath -eq $volumeRoot) {
        throw "$Name must be a dedicated subdirectory, not a volume or share root."
    }
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory)][string]$FirstPath,
        [Parameter(Mandatory)][string]$SecondPath
    )

    $first = Get-NormalizedStoragePath -Path $FirstPath
    $second = Get-NormalizedStoragePath -Path $SecondPath
    if ($first -eq $second) {
        return $true
    }

    $comparison = [System.StringComparison]::OrdinalIgnoreCase
    return $second.StartsWith("$first\", $comparison) -or
        $first.StartsWith("$second\", $comparison)
}

function Set-ProtectedServiceAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Security.Principal.SecurityIdentifier]$ServiceSid,
        [Parameter(Mandatory)][System.Security.AccessControl.FileSystemRights]$ServiceRights,
        [Parameter(Mandatory)][bool]$IsDirectory,
        [bool]$AllowServiceWrites = $false,
        [bool]$InheritDeny = $true
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required file-system item is unavailable: $Path"
    }

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    $acl.PurgeAccessRules($ServiceSid)

    $inheritance = if ($IsDirectory) {
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    } else {
        [System.Security.AccessControl.InheritanceFlags]::None
    }

    $administratorsRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $script:AdministratorsSid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $serviceRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceSid,
        $ServiceRights,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)

    $acl.AddAccessRule($administratorsRule)

    $systemInheritance = if ($IsDirectory) {
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    } else {
        [System.Security.AccessControl.InheritanceFlags]::None
    }
    $systemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $script:SystemSid,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $systemInheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($systemRule)
    $acl.AddAccessRule($serviceRule)

    if (-not $AllowServiceWrites) {
        $deniedRights = [System.Security.AccessControl.FileSystemRights]::Write -bor
            [System.Security.AccessControl.FileSystemRights]::Delete -bor
            [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [System.Security.AccessControl.FileSystemRights]::TakeOwnership
        if ($IsDirectory) {
            $deniedRights = $deniedRights -bor
                [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
        }

        $denyInheritance = if ($InheritDeny) {
            $inheritance
        } else {
            [System.Security.AccessControl.InheritanceFlags]::None
        }
        $denyRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $ServiceSid,
            $deniedRights,
            $denyInheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Deny)
        $acl.AddAccessRule($denyRule)
    }

    Set-Acl -LiteralPath $Path -AclObject $acl
}

if (-not [System.IO.Path]::IsPathRooted($InstallDirectory)) {
    throw "InstallDirectory must be an absolute Windows path."
}

$installDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
Assert-DedicatedPath -Path $installDirectory -Name "InstallDirectory"
$serviceName = "ActivityRecordingService"
$exePath = Join-Path -Path $installDirectory -ChildPath "ActivityRecordingService.exe"
$configPath = Join-Path -Path $installDirectory -ChildPath "appsettings.json"
$requiredFfmpegPath = Join-Path -Path $installDirectory -ChildPath "ffmpeg\ffmpeg.exe"
$requiredModelPath = Join-Path -Path $installDirectory -ChildPath "models\yolo11n.onnx"

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Executable not found: $exePath"
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Configuration not found: $configPath. Copy appsettings.example.json outside source control, rename it to appsettings.json, complete every local value and camera setting, then copy that file here. The installer never creates or overwrites the real configuration."
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service $serviceName is already installed."
}

try {
    $configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}
catch {
    throw "appsettings.json is not valid JSON. Validate and correct the file before installing."
}

$activityConfiguration = $configuration.ActivityRecording
if ($null -eq $activityConfiguration) {
    throw "appsettings.json must contain an ActivityRecording section."
}

$activityEnabled = Get-ConfigurationBoolean -Configuration $activityConfiguration -Name "Enabled"
$ffmpegPath = $null
$modelPath = $null
if ($activityEnabled) {
    $ffmpegPath = Join-InstallPath -BaseDirectory $installDirectory -Path (
        Get-ConfigurationValue -Configuration $activityConfiguration -Name "FfmpegPath" -DefaultValue "ffmpeg/ffmpeg.exe")
    $detectionConfiguration = $activityConfiguration.Detection
    if ($null -eq $detectionConfiguration) {
        throw "ActivityRecording:Detection must be a JSON object when ActivityRecording:Enabled is true."
    }
    $modelPath = Join-InstallPath -BaseDirectory $installDirectory -Path (
        Get-ConfigurationValue -Configuration $detectionConfiguration -Name "ModelPath" -DefaultValue "models/person-yolo11n.onnx")

    if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) {
        throw "The FFmpeg executable configured by appsettings.json is missing. Disabled-first installation can omit FFmpeg, but it is required before enabling activity recording."
    }
    if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
        throw "The ONNX model configured by appsettings.json is missing. Disabled-first installation can omit the model, but it is required before enabling activity recording."
    }
}

$recordingRoot = Get-ConfigurationValue -Configuration $activityConfiguration -Name "RecordingRoot"
if ([string]::IsNullOrWhiteSpace($recordingRoot) -or -not [System.IO.Path]::IsPathRooted($recordingRoot)) {
    throw "ActivityRecording:RecordingRoot must be an absolute local path or UNC path."
}

Assert-DedicatedPath -Path $recordingRoot -Name "ActivityRecording:RecordingRoot"
if (Test-PathsOverlap -FirstPath $installDirectory -SecondPath $recordingRoot) {
    throw "ActivityRecording:RecordingRoot and InstallDirectory must be path-disjoint. Neither path may contain or overlap the other; recordings must not be stored in the install tree."
}

$logDirectory = Join-Path -Path $installDirectory -ChildPath "logs"
$administrators = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
$system = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18")
$serviceIdentity = if ($null -ne $ServiceAccount) {
    [System.Security.Principal.NTAccount]::new($ServiceAccount.UserName)
} else {
    [System.Security.Principal.NTAccount]::new("NT AUTHORITY", "SYSTEM")
}

try {
    $serviceSid = $serviceIdentity.Translate([System.Security.Principal.SecurityIdentifier])
}
catch {
    throw "The configured service account could not be resolved. Verify the account name and domain connectivity before installing."
}

$script:AdministratorsSid = $administrators
$script:SystemSid = $system

$installItems = @(Get-ChildItem -LiteralPath $installDirectory -Recurse -Force)
$reparseItems = @($installItems | Where-Object {
    ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparseItems.Count -gt 0) {
    throw "InstallDirectory contains reparse points. Replace links with real publish files before installing so ACLs cannot be applied through an unexpected target."
}

New-Item -Path $logDirectory -ItemType Directory -Force | Out-Null

foreach ($item in $installItems) {
    $isInsideLogs = Test-PathsOverlap -FirstPath $logDirectory -SecondPath $item.FullName
    if ($isInsideLogs) {
        continue
    }

    Set-ProtectedServiceAcl -Path $item.FullName -ServiceSid $serviceSid `
        -ServiceRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
        -IsDirectory ($item.PSIsContainer) -AllowServiceWrites:$false
}

Set-ProtectedServiceAcl -Path $installDirectory -ServiceSid $serviceSid `
    -ServiceRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
    -IsDirectory $true -AllowServiceWrites:$false -InheritDeny:$false
Set-ProtectedServiceAcl -Path $configPath -ServiceSid $serviceSid `
    -ServiceRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
    -IsDirectory $false -AllowServiceWrites:$false

if ($activityEnabled) {
    foreach ($protectedPath in @($ffmpegPath, $requiredFfmpegPath, $modelPath, $requiredModelPath, $exePath) |
        Where-Object { $null -ne $_ } |
        Select-Object -Unique) {
        Set-ProtectedServiceAcl -Path $protectedPath -ServiceSid $serviceSid `
            -ServiceRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
            -IsDirectory $false -AllowServiceWrites:$false
    }
}
else {
    Set-ProtectedServiceAcl -Path $exePath -ServiceSid $serviceSid `
        -ServiceRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
        -IsDirectory $false -AllowServiceWrites:$false
}

Set-ProtectedServiceAcl -Path $logDirectory -ServiceSid $serviceSid `
    -ServiceRights ([System.Security.AccessControl.FileSystemRights]::Modify) `
    -IsDirectory $true -AllowServiceWrites:$true

New-Item -Path $recordingRoot -ItemType Directory -Force | Out-Null
Set-ProtectedServiceAcl -Path $recordingRoot -ServiceSid $serviceSid `
    -ServiceRights ([System.Security.AccessControl.FileSystemRights]::Modify) `
    -IsDirectory $true -AllowServiceWrites:$true

$binaryPath = '"{0}"' -f $exePath
$serviceParameters = @{
    Name = $serviceName
    BinaryPathName = $binaryPath
    DisplayName = "Activity Recording Service"
    Description = "Records H.264 activity events detected from RTSP cameras."
    StartupType = "Automatic"
}

if ($null -ne $ServiceAccount) {
    $serviceParameters.Credential = $ServiceAccount
}

function Remove-PartialServiceRegistration {
    $partialService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $partialService) {
        return
    }

    try {
        if ($partialService.Status -ne "Stopped") {
            Stop-Service -Name $serviceName
            $partialService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }
    }
    catch {
        Write-Warning "Could not stop partial service registration $serviceName. Stop it manually before deleting the registration."
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Partial service registration removed. appsettings.json, recordings, logs, database, and the model were preserved."
        Write-Host "Resolve the registration error, wait for Windows to complete deletion, then rerun the installer."
    }
    else {
        Write-Warning "Partial service registration could not be deleted (sc.exe exit code $LASTEXITCODE). Run: sc.exe delete $serviceName"
    }
}

$serviceCreated = $false
try {
    New-Service @serviceParameters | Out-Null
    $serviceCreated = $true

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
    if (-not $activityEnabled) {
        Write-Host "Installed ActivityRecordingService in disabled-first mode. To enable it later, install the configured FFmpeg executable and ONNX model, set ActivityRecording:Enabled and each camera Enabled to true, and restart the service."
    }
}
catch {
    if ($serviceCreated) {
        Remove-PartialServiceRegistration
    }

    Write-Host "No appsettings.json, recordings, logs, database, or model files were deleted. Correct the account or service error shown above, confirm the service registration is absent, and rerun the installer."
    throw
}
