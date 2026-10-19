[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid] $InstanceId,
    [Parameter(Mandatory)][string] $InstallRoot,
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][string] $WorkerConfigPath,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $SiteName,
    [Parameter(Mandatory)][string] $HostName,
    [Parameter(Mandatory)][string] $CertificateThumbprint,
    [string] $SchedulerServiceName = "WorkerDock-Scheduler-$($InstanceId.ToString('N').Substring(0,8))",
    [string] $WorkerServiceName = "WorkerDock-Worker-$($InstanceId.ToString('N').Substring(0,8))",
    [string] $SchedulerServiceAccount = "NT SERVICE\WorkerDock-Scheduler-$($InstanceId.ToString('N').Substring(0,8))",
    [string] $WorkerServiceAccount = "NT SERVICE\WorkerDock-Worker-$($InstanceId.ToString('N').Substring(0,8))"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerDock.Deployment.psm1') -Force
Assert-DeploymentHost
Assert-IisAvailable

$root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
$package = Get-VerifiedPackage $PackagePath 'tenant'
try {
    $configRoot = Join-Path $root 'config'
    $tenantConfigPath = Join-Path $configRoot 'tenant.json'
    $workerConfigDestination = Join-Path $configRoot 'worker.json'
    $incomingTenant = Read-DeploymentConfig $ConfigPath
    [void](Assert-ConfigIdentity $incomingTenant 'tenant' $InstanceId)
    $incomingWorker = Get-Content -LiteralPath (Resolve-DeploymentPath $WorkerConfigPath 'WorkerConfigPath') -Raw | ConvertFrom-Json -AsHashtable
    if ([string]$incomingWorker.Tenant.InstanceId -ne $InstanceId.ToString()) { throw 'Worker config tenant identity does not match InstanceId.' }
    $existingMetadataPath = Join-Path $configRoot 'deployment.json'
    $ownsIis = Test-Path -LiteralPath $existingMetadataPath -PathType Leaf
    if ($ownsIis) {
        $existingMetadata = Read-DeploymentMetadata $root $InstanceId
        if ($existingMetadata.kind -ne 'tenant' -or $existingMetadata.siteName -ne $SiteName -or
            $existingMetadata.hostName -ne $HostName -or $existingMetadata.schedulerServiceName -ne $SchedulerServiceName -or
            $existingMetadata.workerServiceName -ne $WorkerServiceName -or
            $existingMetadata.schedulerServiceAccount -ne $SchedulerServiceAccount -or $existingMetadata.workerServiceAccount -ne $WorkerServiceAccount -or
            $existingMetadata.certificateThumbprint -ne $CertificateThumbprint.Replace(' ','').ToUpperInvariant()) {
            throw 'Existing tenant installation identities or IIS binding differ; refusing to replace them.'
        }
        if ([string]$existingMetadata.activeVersion -ne [string]$package.Manifest.version) { throw 'A different package version is installed. Use Upgrade-Instance.' }
        if (Test-Path -LiteralPath $tenantConfigPath) { [void](Assert-ConfigIdentity (Read-DeploymentConfig $tenantConfigPath) 'tenant' $InstanceId) }
        if (Test-Path -LiteralPath $workerConfigDestination) {
            $existingWorkerConfig = Read-DeploymentConfig $workerConfigDestination
            if ([string]$existingWorkerConfig.Worker.InstallationId -ne [string]$incomingWorker.Worker.InstallationId) { throw 'Existing worker installation identity differs; it was preserved.' }
        }
        $resourcesPresent = (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) -and
            (Get-Service -Name $SchedulerServiceName -ErrorAction SilentlyContinue) -and (Get-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue)
        if ($resourcesPresent) { Write-Output "Tenant instance $InstanceId is already installed at version $($package.Manifest.version)."; return }
    }
    foreach ($serviceIdentity in @(
        @{ name = $SchedulerServiceName; account = $SchedulerServiceAccount },
        @{ name = $WorkerServiceName; account = $WorkerServiceAccount }
    )) {
        if (Get-Service -Name $serviceIdentity.name -ErrorAction SilentlyContinue) {
            if (-not $ownsIis) { throw "Windows service name is already in use by an unregistered service: $($serviceIdentity.name)" }
            Assert-OwnedService $serviceIdentity.name $root $serviceIdentity.account
        }
    }
    $tenantConfig = Save-PersistentConfig $ConfigPath $tenantConfigPath $InstanceId 'tenant'
    $release = Install-VerifiedRelease $package $root
    $workerExecutor = Join-Path $release 'worker/executor/WorkerDock.Executor.exe'
    if (-not (Test-Path -LiteralPath $workerExecutor -PathType Leaf)) { throw 'Tenant package does not contain the Windows executor host.' }
    $workerConfig = Save-WorkerConfig $WorkerConfigPath $workerConfigDestination $InstanceId $workerExecutor
    $schedulerEnvironment = 'Production'
    $workerEnvironment = "WorkerDock-$WorkerServiceName"
    Set-ReleaseConfig $tenantConfigPath (Join-Path $release 'web/appsettings.Production.json')
    Set-ReleaseConfig $tenantConfigPath (Join-Path $release 'scheduler/appsettings.Production.json')
    Set-ReleaseConfig $workerConfigDestination (Join-Path $release "worker/appsettings.$workerEnvironment.json") $workerExecutor

    $poolName = "WorkerDock-$($InstanceId.ToString('N').Substring(0,8))-Web"
    Write-DeploymentMetadata $root ([ordered]@{
        kind = 'tenant'; instanceId = $InstanceId.ToString(); activeVersion = [string]$package.Manifest.version
        siteName = $SiteName; poolName = $poolName
        hostName = $HostName; certificateThumbprint = $CertificateThumbprint.Replace(' ','').ToUpperInvariant()
        schedulerServiceName = $SchedulerServiceName; schedulerServiceAccount = $SchedulerServiceAccount
        schedulerServiceEnvironment = $schedulerEnvironment; workerServiceName = $WorkerServiceName; workerServiceAccount = $WorkerServiceAccount
        workerServiceEnvironment = $workerEnvironment
        workerInstallations = @([ordered]@{ serviceName = $WorkerServiceName; serviceAccount = $WorkerServiceAccount
            environment = $workerEnvironment; installationId = [string]$workerConfig.Worker.InstallationId
            configPath = $workerConfigDestination; version = [string]$package.Manifest.version })
    })
    New-HttpsIisSite $SiteName $poolName $HostName $CertificateThumbprint (Join-Path $release 'web') -Owned:$ownsIis
    Ensure-Service $SchedulerServiceName (Join-Path $release 'scheduler/WorkerDock.Scheduler.exe') $SchedulerServiceAccount $schedulerEnvironment $root
    Ensure-Service $WorkerServiceName (Join-Path $release 'worker/WorkerDock.Worker.exe') $WorkerServiceAccount $workerEnvironment $root
    Protect-ConfigDirectory $configRoot @("IIS AppPool\$poolName",$SchedulerServiceAccount,$WorkerServiceAccount)
    $archiveRoot = [string]$tenantConfig.Retention.ArchiveRoot
    if ($archiveRoot) { Protect-SensitiveDirectory $archiveRoot $SchedulerServiceAccount }
} finally { Close-VerifiedPackage $package }
