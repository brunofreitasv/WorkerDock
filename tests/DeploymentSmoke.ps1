[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$configRoot = Join-Path $PSScriptRoot '../examples/config'
$tenantPath = Join-Path $configRoot 'tenant.example.json'
$workerPath = Join-Path $configRoot 'worker.example.json'
$centerPath = Join-Path $configRoot 'center.example.json'
$tenant = Get-Content -LiteralPath $tenantPath -Raw | ConvertFrom-Json -AsHashtable
$worker = Get-Content -LiteralPath $workerPath -Raw | ConvertFrom-Json -AsHashtable
$center = Get-Content -LiteralPath $centerPath -Raw | ConvertFrom-Json -AsHashtable

if (-not $tenant.Contains('Tenant')) { throw 'Tenant example does not use the Tenant host configuration section.' }
if (-not $worker.Contains('Tenant') -or -not $worker.Contains('Worker') -or -not $worker.Contains('Executor')) {
    throw 'Worker example does not include Tenant, Worker, and Executor host configuration sections.'
}
if (-not $center.Contains('Central')) { throw 'Center example does not use the Central host configuration section.' }

$sqlKeys = @('Host', 'Port', 'Database', 'User', 'PasswordReference')
foreach ($entry in @(
    @{ Name = 'tenant'; Section = $tenant.Tenant },
    @{ Name = 'worker'; Section = $worker.Tenant }
)) {
    if ($entry.Section.Contains('ConnectionString')) { throw "$($entry.Name) example must not contain ConnectionString." }
    if ($entry.Section.Adapter -ne 'Sql') { throw "$($entry.Name) example must configure the Sql adapter." }
    $sql = $entry.Section.Sql
    if (-not $sql) { throw "$($entry.Name) example is missing its SQL settings." }
    foreach ($key in $sqlKeys) {
        if (-not $sql.Contains($key) -or $null -eq $sql[$key] -or [string]::IsNullOrWhiteSpace([string]$sql[$key])) {
            throw "$($entry.Name) example is missing Tenant:Sql:$key."
        }
    }
    if ($sql.Port -lt 1 -or $sql.Port -gt 65535) { throw "$($entry.Name) example has an invalid Tenant:Sql:Port." }
}
if ($tenant.Tenant.Sql.PasswordReference -ne $worker.Tenant.Sql.PasswordReference) {
    throw 'Tenant and worker examples must use the same runtime password reference.'
}

$migrationScript = Join-Path $PSScriptRoot '../scripts/Invoke-TenantMigration.ps1'
if (-not (Test-Path -LiteralPath $migrationScript -PathType Leaf)) { throw 'Optional tenant migration script is missing.' }
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $migrationScript).Path, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count) { throw "PowerShell parse failed for Invoke-TenantMigration.ps1: $($errors[0].Message)" }

$removedPaths = @(
    '../deploy/Install-Tenant.ps1',
    '../deploy/Install-Worker.ps1',
    '../deploy/Install-Center.ps1',
    '../deploy/Upgrade-Instance.ps1',
    '../deploy/Uninstall-Instance.ps1',
    '../deploy/Build-Packages.ps1',
    '../deploy/WorkerDock.Deployment.psm1',
    '../deploy/config/migrator.example.json',
    '../tests/deploy/tenant-sql-vault.Tests.ps1',
    '../tests/deploy/package-contents.Tests.ps1',
    '../tests/deploy/center-storage.Tests.ps1',
    '../src/WorkerDock.DatabaseMigrator/WorkerDock.DatabaseMigrator.csproj'
)
foreach ($relativePath in $removedPaths) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $relativePath)) { throw "Removed deployment asset still exists: $relativePath" }
}

Write-Output 'pass'
