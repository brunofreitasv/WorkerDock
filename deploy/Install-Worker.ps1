[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid] $InstanceId,
    [Parameter(Mandatory)][string] $InstallRoot,
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,80}$')][string] $ServiceName,
    [string] $ServiceAccount = "NT SERVICE\$ServiceName"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerDock.Deployment.psm1') -Force
Assert-DeploymentHost

$root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
$metadata = Read-DeploymentMetadata $root $InstanceId
if ($metadata.kind -ne 'tenant') { throw 'Install-Worker requires a tenant installation root.' }
$package = Get-VerifiedPackage $PackagePath 'tenant'
try {
    if ([string]$metadata.activeVersion -ne [string]$package.Manifest.version) { throw 'Worker package version must match the active tenant release. Use Upgrade-Instance for a release change.' }
    $tenantConfigPath = Join-Path $root 'config/tenant.json'
    if (-not (Test-Path -LiteralPath $tenantConfigPath -PathType Leaf)) { throw 'Tenant configuration is missing; install the tenant first.' }
    $tenantConfig = Read-DeploymentConfig $tenantConfigPath
    [void](Assert-ConfigIdentity $tenantConfig 'tenant' $InstanceId)
    $release = Install-VerifiedRelease $package $root
    $executor = Join-Path $release 'worker/executor/WorkerDock.Executor.exe'
    if (-not (Test-Path -LiteralPath $executor -PathType Leaf)) { throw 'Tenant package does not contain the Windows executor host.' }
    $workerConfigPath = Join-Path $root "config/workers/$ServiceName.json"
    $workerConfig = Save-WorkerConfig $ConfigPath $workerConfigPath $InstanceId $executor
    $environment = "WorkerDock-$ServiceName"
    $existingWorker = @($metadata.workerInstallations | Where-Object { $_.serviceName -eq $ServiceName })
    if ($existingWorker.Count -gt 0) {
        if ([string]$existingWorker[0].installationId -ne [string]$workerConfig.Worker.InstallationId -or
            [string]$existingWorker[0].serviceAccount -ne $ServiceAccount) {
            throw "Service name $ServiceName is already registered for a different worker installation identity."
        }
        Write-Output "Worker $ServiceName is already installed for tenant $InstanceId."; return
    }
    Set-ReleaseConfig $workerConfigPath (Join-Path $release "worker/appsettings.$environment.json") $executor
    Ensure-Service $ServiceName (Join-Path $release 'worker/WorkerDock.Worker.exe') $ServiceAccount $environment $root
    Protect-ConfigDirectory (Join-Path $root 'config/workers') @($ServiceAccount)
    $metadataPath = Join-Path $root 'config/deployment.json'
    $existingWorker = @($metadata.workerInstallations | Where-Object { $_.serviceName -eq $ServiceName })
    if ($existingWorker.Count -gt 0 -and [string]$existingWorker[0].installationId -ne [string]$workerConfig.Worker.InstallationId) {
        throw "Service name $ServiceName is already registered for a different worker installation identity."
    }
    $newWorker = [ordered]@{
        serviceName = $ServiceName; serviceAccount = $ServiceAccount; installationId = [string]$workerConfig.Worker.InstallationId
        environment = $environment; configPath = $workerConfigPath; version = [string]$package.Manifest.version
    }
    if ($existingWorker.Count -eq 0) { $metadata.workerInstallations = @($metadata.workerInstallations) + @($newWorker) }
    else {
        $metadata.workerInstallations = @($metadata.workerInstallations | Where-Object { $_.serviceName -ne $ServiceName }) + @($newWorker)
    }
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
} finally { Close-VerifiedPackage $package }
