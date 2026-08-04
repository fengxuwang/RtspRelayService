#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serviceName = "RtspRelayService"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($null -eq $service) {
    Write-Host "Service $serviceName is not installed."
    return
}

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $serviceName
    $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

& sc.exe delete $serviceName
if ($LASTEXITCODE -ne 0) {
    throw "Failed to delete service registration (sc.exe exit code $LASTEXITCODE)."
}

Write-Host "Service removed. Configuration and logs were preserved."
