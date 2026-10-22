$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $root 'deploy/WorkerDock.Deployment.psm1') -Force -DisableNameChecking
function Assert-True([bool] $Value, [string] $Message) { if (-not $Value) { throw $Message } }
function Assert-Throws([scriptblock] $Action, [string] $Message) { try { & $Action } catch { return }; throw $Message }
Assert-True ($null -ne (Get-Command Assert-TenantSqlConfig -ErrorAction SilentlyContinue)) 'Assert-TenantSqlConfig must be exported.'
$id = [guid]::NewGuid().ToString()
function New-Config { return @{ Tenant = @{ InstanceId = $id; Sql = @{ Host = 'sql.example.test'; Port = 1433; Database = 'TenantDb'; User = 'runtime'; PasswordReference = 'tenant.database.password' } } } }
$config = New-Config
Assert-TenantSqlConfig $config 'Tenant'
Assert-True ((Assert-ConfigIdentity $config tenant $id) -eq 'Tenant') 'Tenant identity rejected vault SQL settings.'
foreach ($field in @('Host','Port','Database','User','PasswordReference')) {
    $invalid = New-Config; $invalid.Tenant.Sql.Remove($field)
    Assert-Throws { Assert-TenantSqlConfig $invalid 'Tenant' } "Missing $field was accepted."
}
foreach ($value in @(0,65536,'invalid',1.5)) {
    $invalid = New-Config; $invalid.Tenant.Sql.Port = $value
    Assert-Throws { Assert-TenantSqlConfig $invalid 'Tenant' } 'Invalid SQL port was accepted.'
}
foreach ($value in @('host,1433','host\instance','host;Password=x',"host`n")) {
    $invalid = New-Config; $invalid.Tenant.Sql.Host = $value
    Assert-Throws { Assert-TenantSqlConfig $invalid 'Tenant' } 'Invalid SQL host was accepted.'
}
foreach ($value in @('', 'secret/path', ('x' * 129))) {
    $invalid = New-Config; $invalid.Tenant.Sql.PasswordReference = $value
    Assert-Throws { Assert-TenantSqlConfig $invalid 'Tenant' } 'Invalid password reference was accepted.'
}
foreach ($value in @('', $null, 'legacy')) {
    $invalid = New-Config; $invalid.Tenant.ConnectionString = $value
    Assert-Throws { Assert-TenantSqlConfig $invalid 'Tenant' } 'Present legacy connection string was accepted.'
    Assert-Throws { Assert-ConfigIdentity $invalid tenant $id } 'Tenant identity accepted legacy connection string.'
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WorkerDock-vault-deploy-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temp) | Out-Null
    $worker = New-Config
    $worker.Worker = @{ InstallationId = [guid]::NewGuid().ToString(); CatalogDigest = ('a' * 64); HostCeiling = 2; DrainSeconds = 30 }
    $workerPath = Join-Path $temp 'worker-source.json'
    $worker | ConvertTo-Json -Depth 8 | Set-Content $workerPath
    $null = Save-WorkerConfig $workerPath (Join-Path $temp 'worker.json') $id
    $worker.Tenant.Sql.Remove('Port')
    $worker | ConvertTo-Json -Depth 8 | Set-Content $workerPath
    Assert-Throws { Save-WorkerConfig $workerPath (Join-Path $temp 'invalid.json') $id } 'Worker accepted incomplete SQL settings.'
    $worker.Tenant.Sql.Port = 1433
    $worker | ConvertTo-Json -Depth 8 | Set-Content $workerPath
    $persistedWorker = Get-Content (Join-Path $temp 'worker.json') -Raw | ConvertFrom-Json -AsHashtable
    $persistedWorker.Tenant.ConnectionString = ''
    $persistedWorker | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $temp 'worker.json')
    Assert-Throws { Save-WorkerConfig $workerPath (Join-Path $temp 'worker.json') $id } 'Worker reused a persisted legacy connection string.'
    # Evaluate the real upgrade preflight with host checks removed, stopping before metadata/IIS access.
    $upgradePath = Join-Path $root 'deploy/Upgrade-Instance.ps1'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($upgradePath, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) 'Upgrade script does not parse.'
    $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'MigratorConfigPath' })
    Assert-True ($parameter.Count -eq 1) 'Upgrade lacks MigratorConfigPath.'
    Assert-True ($parameter[0].Extent.Text -notmatch 'Mandatory') 'Center upgrade must not require MigratorConfigPath.'
    $source = Get-Content $upgradePath -Raw
    $preflight = $source.Substring(0, $source.IndexOf('$metadata = Read-DeploymentMetadata')) -replace '(?m)^Assert-DeploymentHost\s*$', ''
    $preflightPath = Join-Path $temp 'preflight.ps1'
    $preflight = $preflight.Replace("(Join-Path `$PSScriptRoot 'WorkerDock.Deployment.psm1')", "'$((Join-Path $root 'deploy/WorkerDock.Deployment.psm1').Replace("'", "''"))'")
    $preflight = $preflight.Replace(' -Force', ' -Force -DisableNameChecking')
    $preflight | Set-Content $preflightPath
    $tenantPath = Join-Path $temp 'tenant.json'; (New-Config) | ConvertTo-Json -Depth 8 | Set-Content $tenantPath
    $argsForUpgrade = @{ InstallRoot = $temp; ConfigPath = $tenantPath; PackagePath = $temp; BackupPath = (Join-Path $temp 'backup.bak') }
    Assert-Throws { & $preflightPath @argsForUpgrade } 'Tenant upgrade accepted missing MigratorConfigPath.'
    Assert-Throws { & $preflightPath @argsForUpgrade -MigratorConfigPath (Join-Path $temp 'missing.json') } 'Tenant upgrade accepted missing migrator file.'
    $migratorPath = Join-Path $temp 'migrator.json'
    @{ Migrator = @{ Sql = (New-Config).Tenant.Sql } } | ConvertTo-Json -Depth 8 | Set-Content $migratorPath
    & $preflightPath @argsForUpgrade -MigratorConfigPath $migratorPath
    $savedLegacy = [Environment]::GetEnvironmentVariable('WORKERDOCK_SQL')
    try {
        [Environment]::SetEnvironmentVariable('WORKERDOCK_SQL', '')
        Assert-Throws { & $preflightPath @argsForUpgrade -MigratorConfigPath $migratorPath } 'Tenant upgrade accepted an empty legacy SQL variable.'
        [Environment]::SetEnvironmentVariable('WORKERDOCK_SQL', 'synthetic-legacy')
        Assert-Throws { & $preflightPath @argsForUpgrade -MigratorConfigPath $migratorPath } 'Tenant upgrade accepted a legacy SQL variable.'
    } finally { [Environment]::SetEnvironmentVariable('WORKERDOCK_SQL', $savedLegacy) }
    @{ Migrator = @{ Sql = @{ Host = 'sql.example.test' } } } | ConvertTo-Json -Depth 8 | Set-Content $migratorPath
    Assert-Throws { & $preflightPath @argsForUpgrade -MigratorConfigPath $migratorPath } 'Tenant upgrade accepted incomplete migrator settings.'
    $centerPath = Join-Path $temp 'center.json'; @{ Central = @{ InstanceId = $id } } | ConvertTo-Json | Set-Content $centerPath
    $argsForUpgrade.ConfigPath = $centerPath
    & $preflightPath @argsForUpgrade
    $commands = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
    $prepare = @($commands | Where-Object { $_.Extent.Text -match '& dotnet .* prepare-upgrade tenant' })
    $apply = @($commands | Where-Object { $_.Extent.Text -match '& dotnet .* apply tenant' })
    $tenantInstall = @($commands | Where-Object { $_.GetCommandName() -eq 'Install-VerifiedRelease' })[-1]
    Assert-True ($prepare.Count -eq 1 -and $apply.Count -eq 1) 'Upgrade must invoke preparation and apply.'
    Assert-True ($prepare[0].Extent.StartOffset -lt $tenantInstall.Extent.StartOffset -and $apply[0].Extent.StartOffset -gt $tenantInstall.Extent.StartOffset) 'Preparation must precede release install; apply must follow it.'
    Assert-True ($source -notmatch 'SqlConnection|WORKERDOCK_SQL\s*=') 'Upgrade must not create SQL connections or hand off a connection string.'
    Write-Output 'pass'
} finally { if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
