[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid] $InstanceId,
    [Parameter(Mandatory)][string] $InstallRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerDock.Deployment.psm1') -Force
Assert-DeploymentHost

$root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
$metadata = Read-DeploymentMetadata $root $InstanceId
Assert-IisAvailable

$serviceNames = @()
$serviceAccounts = @{}
if ($metadata.kind -eq 'tenant') {
    $serviceNames = @($metadata.schedulerServiceName, $metadata.workerServiceName) + @($metadata.workerInstallations | ForEach-Object serviceName)
    if ($metadata.schedulerServiceName) { $serviceAccounts[[string]$metadata.schedulerServiceName] = [string]$metadata.schedulerServiceAccount }
    if ($metadata.workerServiceName) { $serviceAccounts[[string]$metadata.workerServiceName] = [string]$metadata.workerServiceAccount }
    foreach ($worker in @($metadata.workerInstallations)) { if ($worker.serviceName) { $serviceAccounts[[string]$worker.serviceName] = [string]$worker.serviceAccount } }
} elseif ($metadata.kind -ne 'center') {
    throw "Unsupported WorkerDock installation kind: $($metadata.kind)"
}
foreach ($name in $serviceNames | Where-Object { $_ }) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) { Assert-OwnedService $name $root $serviceAccounts[[string]$name] }
    if ($service -and $service.Status -ne 'Stopped') { throw "Service $name is still running. Stop it after an operator-managed drain before uninstalling." }
}
$site = Get-Website -Name $metadata.siteName -ErrorAction SilentlyContinue
if ($site -and $site.State -ne 'Stopped') { throw 'IIS site is still running. Stop it before uninstalling.' }
if ($metadata.poolName -and (Test-Path -LiteralPath "IIS:\AppPools\$($metadata.poolName)") -and
    (Get-WebAppPoolState -Name $metadata.poolName).Value -ne 'Stopped') { throw 'IIS application pool is still running. Stop it before uninstalling.' }

foreach ($name in $serviceNames | Where-Object { $_ }) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Assert-OwnedService $name $root $serviceAccounts[[string]$name]
        & sc.exe delete $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not remove Windows service registration $name." }
    }
}
if ($site) { Remove-Website -Name $metadata.siteName }
if ($metadata.poolName -and (Test-Path -LiteralPath "IIS:\AppPools\$($metadata.poolName)")) { Remove-WebAppPool -Name $metadata.poolName }
Write-Output "Unregistered WorkerDock $($metadata.kind) instance $InstanceId. Configuration, data, database, backups, and release files were preserved under $root."
