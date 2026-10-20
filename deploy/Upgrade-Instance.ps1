[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InstallRoot,
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $BackupPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerDock.Deployment.psm1') -Force
Assert-DeploymentHost

$root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
$config = Read-DeploymentConfig $ConfigPath
$kind = if ($config.Contains('Tenant')) { 'tenant' } elseif ($config.Contains('Central')) { 'center' } else { throw 'Configuration must contain Tenant or Central settings.' }
$instanceSection = if ($kind -eq 'tenant') { 'Tenant' } else { 'Central' }
$instanceId = [string](Get-ConfigValue $config $instanceSection 'InstanceId')
if (-not [guid]::TryParse($instanceId, [ref]([guid]::Empty))) { throw "$instanceSection.InstanceId must be a GUID." }
$metadata = Read-DeploymentMetadata $root $instanceId
if ($metadata.kind -ne $kind) { throw 'Package kind and installed instance kind do not match.' }
$storedConfig = Join-Path $root $(if ($kind -eq 'tenant') { 'config/tenant.json' } else { 'config/center.json' })
$existingConfig = Read-DeploymentConfig $storedConfig
[void](Assert-ConfigIdentity $existingConfig $kind $instanceId)
$package = Get-VerifiedPackage $PackagePath $kind
$backupFullPath = Resolve-DeploymentPath $BackupPath 'BackupPath'
try {
    $statePath = Join-Path $root 'config/upgrade-state.json'
    if (Test-Path -LiteralPath $statePath -PathType Leaf) { throw "An incomplete upgrade journal exists at $statePath; inspect backups and restore/reconcile it before retrying." }
    if ([string]$package.Manifest.version -eq [string]$metadata.activeVersion) { throw 'Package version is already active.' }

    Assert-IisAvailable
    if ($kind -eq 'tenant') {
        foreach ($name in @($metadata.schedulerServiceName, $metadata.workerServiceName) + @($metadata.workerInstallations | ForEach-Object serviceName)) {
            if (-not $name) { continue }
            $service = Get-Service -Name $name -ErrorAction SilentlyContinue
            if (-not $service) { throw "Installed service is missing: $name" }
            if ($service.Status -ne 'Stopped') {
                throw "Unsafe upgrade state: service $name is running. Operator must inspect executions, decide how to handle infinite jobs, block admission, and stop services first; this script cannot drain jobs."
            }
        }
    }
    $site = Get-Website -Name $metadata.siteName -ErrorAction SilentlyContinue
    if (-not $site) { throw "Installed IIS site is missing: $($metadata.siteName)" }
    if ($site.State -ne 'Stopped') { throw 'Unsafe upgrade state: IIS site must be stopped after operator-managed admission blocking; this script cannot block admission or drain requests.' }
    if ((Get-WebAppPoolState -Name $metadata.poolName).Value -ne 'Stopped') { throw 'Unsafe upgrade state: IIS application pool must be stopped first.' }

    if ($kind -eq 'center') {
        if ([IO.Path]::GetExtension($backupFullPath) -ne '.zip') { throw 'Center BackupPath must be a new .zip archive path.' }
        if (Test-Path -LiteralPath $backupFullPath) { throw 'BackupPath already exists; refusing to overwrite a center backup.' }
        $stateCompatibility = $package.Manifest.stateFormatCompatibility
        if ($null -eq $stateCompatibility -or [int]$stateCompatibility.minimum -gt 1 -or [int]$stateCompatibility.maximum -lt 1) {
            throw 'Center package does not support persistent state format 1.'
        }
        $stateDirectory = if ($metadata.stateDirectory) { [string]$metadata.stateDirectory } else { Assert-CenterDataDirectory $existingConfig $root }
        if ((Assert-CenterDataDirectory $existingConfig $root) -ne [IO.Path]::GetFullPath($stateDirectory)) { throw 'Configured center state directory differs from installed metadata.' }
        $oldRelease = Join-Path $root "releases/$($metadata.activeVersion)"
        if (-not (Test-Path -LiteralPath $oldRelease -PathType Container)) { throw "Active release directory is missing: $oldRelease" }
        $previousVersion = [string]$metadata.activeVersion
        [void](New-CenterBackupArchive $stateDirectory $storedConfig $oldRelease $backupFullPath $instanceId $previousVersion)
        $release = Install-VerifiedRelease $package $root
        Set-ReleaseConfig $storedConfig (Join-Path $release 'web/appsettings.Production.json')
        Set-ItemProperty "IIS:\Sites\$($metadata.siteName)" -Name physicalPath -Value (Join-Path $release 'web')
        $metadata.activeVersion = [string]$package.Manifest.version
        $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'config/deployment.json') -Encoding utf8
        Write-Output "Center upgrade staged to version $($package.Manifest.version). IIS remains stopped. Verified state/config/release backup: $backupFullPath. Review the backup and start the application manually; readiness is not claimed."
        return
    }

    if (-not $backupFullPath.StartsWith('\\') -or [IO.Path]::GetExtension($backupFullPath) -ne '.bak') {
        throw 'BackupPath must be a new UNC .bak path accessible to the SQL Server service account.'
    }
    if (Test-Path -LiteralPath $backupFullPath) { throw 'BackupPath already exists; refusing to overwrite a database backup.' }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $backupFullPath)) | Out-Null
    $oldRelease = Join-Path $root "releases/$($metadata.activeVersion)"
    if (-not (Test-Path -LiteralPath $oldRelease -PathType Container)) { throw "Active release directory is missing: $oldRelease" }
    $releaseBackupPath = Join-Path (Split-Path -Parent $backupFullPath) ([IO.Path]::GetFileNameWithoutExtension($backupFullPath) + '-application.zip')
    if (Test-Path -LiteralPath $releaseBackupPath) { throw "Application backup already exists: $releaseBackupPath" }

    $connection = [string](Get-ConfigValue $existingConfig $instanceSection 'ConnectionString')
    $migratorRoot = Join-Path $package.Root 'tools/database-migrator'
    $sqlClient = Join-Path $migratorRoot 'Microsoft.Data.SqlClient.dll'
    $migrator = Join-Path $migratorRoot 'WorkerDock.DatabaseMigrator.dll'
    if (-not (Test-Path -LiteralPath $sqlClient -PathType Leaf) -or -not (Test-Path -LiteralPath $migrator -PathType Leaf)) { throw 'Package database migrator or SQL client is missing.' }
    Add-Type -Path $sqlClient
    $builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($connection)
    if ([string]::IsNullOrWhiteSpace($builder.InitialCatalog)) { throw 'Configured connection string must name a database.' }
    $escapedDatabase = '[' + $builder.InitialCatalog.Replace(']', ']]') + ']'
    $sqlConnection = [Microsoft.Data.SqlClient.SqlConnection]::new($connection)
    try {
        $sqlConnection.Open()
        $backupCommand = $sqlConnection.CreateCommand()
        $backupCommand.CommandTimeout = 0
        $backupCommand.CommandText = "BACKUP DATABASE $escapedDatabase TO DISK = @backup WITH COPY_ONLY, CHECKSUM; RESTORE VERIFYONLY FROM DISK = @backup WITH CHECKSUM;"
        $null = $backupCommand.Parameters.Add('@backup', [Microsoft.Data.SqlClient.SqlDbType]::NVarChar, 4000)
        $backupCommand.Parameters['@backup'].Value = $backupFullPath
        $null = $backupCommand.ExecuteNonQuery()
    } finally { if ($sqlConnection.State -ne [Data.ConnectionState]::Closed) { $sqlConnection.Close() }; $sqlConnection.Dispose() }
    if (-not (Test-Path -LiteralPath $backupFullPath -PathType Leaf) -or (Get-Item -LiteralPath $backupFullPath).Length -le 0) {
        throw 'SQL Server did not produce a verifiable backup at BackupPath.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($releaseBackupPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $oldRelease -File -Recurse | Where-Object { $_.Name -notlike 'appsettings*.json' }) {
            $relative = $file.FullName.Substring($oldRelease.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    [void](Assert-DirectoryArchive $releaseBackupPath $oldRelease)

    $schemaManifestPath = Join-Path $migratorRoot 'database/tenant/manifest.json'
    if (-not (Test-Path -LiteralPath $schemaManifestPath -PathType Leaf)) { throw 'Package migration manifest is missing.' }
    $schemaEntries = @(Get-Content -LiteralPath $schemaManifestPath -Raw | ConvertFrom-Json)
    if ($schemaEntries.Count -eq 0) { throw 'Package migration manifest contains no target migration.' }
    $latestMigrationId = [string]$schemaEntries[-1].id
    $latestSchemaVersion = [int]([regex]::Match($latestMigrationId, '^\d+').Value)
    if ($latestSchemaVersion -lt [int]$package.Manifest.schemaCompatibility.minimum -or
        $latestSchemaVersion -gt [int]$package.Manifest.schemaCompatibility.maximum) {
        throw "Latest package migration schema $latestSchemaVersion is outside the package compatibility range."
    }
    $applied = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $sqlConnection = [Microsoft.Data.SqlClient.SqlConnection]::new($connection)
    try {
        $sqlConnection.Open()
        $historyCommand = $sqlConnection.CreateCommand()
        $historyCommand.CommandText = 'SELECT MigrationId, UpChecksum FROM [workerdock].SchemaHistory'
        $reader = $historyCommand.ExecuteReader()
        try { while ($reader.Read()) { $applied[[string]$reader.GetString(0)] = [string]$reader.GetString(1) } }
        finally { $reader.Dispose() }
    } finally { if ($sqlConnection.State -ne [Data.ConnectionState]::Closed) { $sqlConnection.Close() }; $sqlConnection.Dispose() }
    $sawGap = $false
    $lastApplied = $null
    foreach ($entry in $schemaEntries) {
        $migrationId = [string]$entry.id
        if ($applied.ContainsKey($migrationId)) {
            if ($sawGap) { throw 'Database migration history is not a contiguous prefix of this package manifest.' }
            if ($applied[$migrationId] -ne [string]$entry.upSha256) { throw "Applied migration checksum differs from this package: $migrationId" }
            $lastApplied = $migrationId
        } else { $sawGap = $true }
    }
    if (-not $lastApplied) { throw 'No known migration is applied to the configured database.' }
    foreach ($migrationId in $applied.Keys) {
        if (-not ($schemaEntries | Where-Object id -CEQ $migrationId)) { throw "Database contains migration absent from this package manifest: $migrationId" }
    }
    $currentSchemaVersion = [int]([regex]::Match($lastApplied, '^\d+').Value)
    if ($currentSchemaVersion -lt [int]$package.Manifest.schemaCompatibility.minimum -or
        $currentSchemaVersion -gt [int]$package.Manifest.schemaCompatibility.maximum) {
        throw "Database schema version $currentSchemaVersion is outside package compatibility range $($package.Manifest.schemaCompatibility.minimum)-$($package.Manifest.schemaCompatibility.maximum)."
    }

    $release = Install-VerifiedRelease $package $root
    if ($kind -eq 'tenant') {
        Set-ReleaseConfig $storedConfig (Join-Path $release 'web/appsettings.Production.json')
        Set-ReleaseConfig $storedConfig (Join-Path $release 'scheduler/appsettings.Production.json')
        $workerConfigs = @($metadata.workerInstallations)
        foreach ($workerRecord in $workerConfigs) {
            $workerFile = [string]$workerRecord.configPath
            if (-not (Test-Path -LiteralPath $workerFile -PathType Leaf)) { throw "Worker configuration is missing: $workerFile" }
            $worker = Read-DeploymentConfig $workerFile
            $executorPath = Join-Path $release 'worker/executor/WorkerDock.Executor.exe'
            $environment = [string]$workerRecord.environment
            if ([string]::IsNullOrWhiteSpace($environment)) { $environment = "WorkerDock-$($workerRecord.serviceName)" }
            Set-ReleaseConfig $workerFile (Join-Path $release "worker/appsettings.$environment.json") $executorPath
        }
    } else {
        Set-ReleaseConfig $storedConfig (Join-Path $release 'web/appsettings.Production.json')
    }

    $journal = [ordered]@{ instanceId = $instanceId; previousVersion = $metadata.activeVersion; targetVersion = $package.Manifest.version
        backupPath = $backupFullPath; sqlBackupVerified = $true; applicationBackupPath = $releaseBackupPath; applicationBackupVerified = $true
        startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); step = 'migrating'; kind = $kind }
    $journal | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    $targetMigration = $latestMigrationId
    $env:WORKERDOCK_SQL = $connection
    $env:WORKERDOCK_INSTANCE_ID = $instanceId
    try {
        & dotnet $migrator apply tenant $targetMigration
        if ($LASTEXITCODE -ne 0) { throw "Database migration failed with exit code $LASTEXITCODE. Backup retained at $backupFullPath." }
    } finally { Remove-Item Env:WORKERDOCK_SQL -ErrorAction SilentlyContinue; Remove-Item Env:WORKERDOCK_INSTANCE_ID -ErrorAction SilentlyContinue }

    $journal.step = 'switching-release'
    $journal | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    if ($kind -eq 'tenant') {
        foreach ($serviceName in @($metadata.schedulerServiceName, $metadata.workerServiceName)) {
            if ($serviceName) {
                $accountKey = if ($serviceName -eq $metadata.schedulerServiceName) { 'schedulerServiceAccount' } else { 'workerServiceAccount' }
                $account = [string]$metadata[$accountKey]
                $exeName = if ($serviceName -eq $metadata.schedulerServiceName) { 'WorkerDock.Scheduler.exe' } else { 'WorkerDock.Worker.exe' }
                $subdir = if ($serviceName -eq $metadata.schedulerServiceName) { 'scheduler' } else { 'worker' }
                $environmentKey = if ($serviceName -eq $metadata.schedulerServiceName) { 'schedulerServiceEnvironment' } else { 'workerServiceEnvironment' }
                $environment = [string]$metadata[$environmentKey]
                if (-not $environment) { $environment = 'Production' }
                Ensure-Service $serviceName (Join-Path $release "$subdir/$exeName") $account $environment $root
            }
        }
        foreach ($worker in @($metadata.workerInstallations)) {
            if ($worker.serviceName) {
                $environment = [string]$worker.environment
                if (-not $environment) { $environment = "WorkerDock-$($worker.serviceName)" }
                Ensure-Service $worker.serviceName (Join-Path $release 'worker/WorkerDock.Worker.exe') ([string]$worker.serviceAccount) $environment $root
            }
        }
        Set-ItemProperty "IIS:\Sites\$($metadata.siteName)" -Name physicalPath -Value (Join-Path $release 'web')
    } else { Set-ItemProperty "IIS:\Sites\$($metadata.siteName)" -Name physicalPath -Value (Join-Path $release 'web') }
    Grant-DirectoryAccess (Join-Path $release 'web') "IIS AppPool\$($metadata.poolName)" 'RX'
    $metadata.activeVersion = [string]$package.Manifest.version
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'config/deployment.json') -Encoding utf8
    Remove-Item -LiteralPath $statePath -Force
    Write-Output "Upgrade staged to version $($package.Manifest.version). Services and IIS remain stopped. Database backup: $backupFullPath. Application backup: $releaseBackupPath. Review both backups and start the application manually; readiness is not claimed."
} finally { Close-VerifiedPackage $package }
