[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [int] $TenantSchemaMinVersion = 1,
    [int] $TenantSchemaMaxVersion = 8,
    [int] $CenterSchemaMinVersion = 1,
    [int] $CenterSchemaMaxVersion = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredPath([string] $RelativePath) {
    $path = Join-Path $SourceRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required packaging input is missing: $RelativePath"
    }
    return (Resolve-Path -LiteralPath $path).Path
}

function Publish-Project([string] $RelativeProject, [string] $Destination) {
    $project = Get-RequiredPath $RelativeProject
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & dotnet publish $project --configuration $Configuration --output $Destination --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $RelativeProject (exit code $LASTEXITCODE)."
    }
}

function Copy-Frontend([string] $WorkspaceName, [string] $WebRoot, [string] $DistPath) {
    $relativeApp = switch ($WorkspaceName) {
        '@workerdock/tenant' { 'frontend/apps/tenant' }
        '@workerdock/center' { 'frontend/apps/center' }
        default { throw "Unsupported frontend workspace: $WorkspaceName" }
    }
    $app = Get-RequiredPath $relativeApp
    if (-not (Test-Path -LiteralPath (Join-Path $app 'package.json'))) {
        throw "Frontend package.json is missing: $relativeApp/package.json"
    }
    New-Item -ItemType Directory -Path $DistPath -Force | Out-Null
    & node (Join-Path $frontendRoot 'scripts/licenses.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Frontend third-party notice generation failed.' }
    # Both Vite configs default to source-tree wwwroot. The CLI override directs this
    # release build to isolated staging, leaving development/runtime source untouched.
    & npm.cmd --prefix $frontendRoot run build --workspace $WorkspaceName -- --outDir $DistPath --configLoader runner
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed for $WorkspaceName (exit code $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath $DistPath)) { throw "Frontend build did not produce staging output: $WorkspaceName" }
    if (-not (Test-Path -LiteralPath (Join-Path $DistPath 'index.html'))) { throw "Frontend staging output has no index.html: $WorkspaceName" }
    $wwwroot = Join-Path $WebRoot 'wwwroot'
    New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
    Copy-Item -Path (Join-Path $DistPath '*') -Destination $wwwroot -Recurse -Force
}

function Write-PackageManifest([string] $PackageRoot, [string] $Kind, [int] $MinSchema, [int] $MaxSchema) {
    $rootManifest = Join-Path $PackageRoot 'manifest.json'
    $files = @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse |
        Where-Object { $_.FullName -ne $rootManifest } |
        Sort-Object { $_.FullName.Substring($PackageRoot.Length).TrimStart([char[]]@('\', '/')) } |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($PackageRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                sizeBytes = $_.Length
            }
        })
    $manifest = [ordered]@{
        product = 'WorkerDock'
        packageKind = $Kind
        version = $Version
        components = if ($Kind -eq 'tenant') {
            [ordered]@{ web = $Version; scheduler = $Version; worker = $Version; executor = $Version; migrator = $Version }
        } else {
            [ordered]@{ web = $Version; migrator = $Version }
        }
        schemaCompatibility = [ordered]@{ minimum = $MinSchema; maximum = $MaxSchema }
        layout = if ($Kind -eq 'tenant') {
            [ordered]@{ web = 'web/'; scheduler = 'scheduler/'; worker = 'worker/'; executor = 'worker/executor/'; migrator = 'tools/database-migrator/' }
        } else {
            [ordered]@{ web = 'web/'; migrator = 'tools/database-migrator/' }
        }
        files = $files
    }
    $manifestPath = Join-Path $PackageRoot 'manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

if ($TenantSchemaMinVersion -lt 1 -or $TenantSchemaMaxVersion -lt $TenantSchemaMinVersion) {
    throw 'Tenant schema compatibility window must be a positive inclusive range.'
}
if ($CenterSchemaMinVersion -lt 1 -or $CenterSchemaMaxVersion -lt $CenterSchemaMinVersion) {
    throw 'Center schema compatibility window must be a positive inclusive range.'
}

$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$frontendRoot = Get-RequiredPath 'frontend'
if (-not (Test-Path -LiteralPath (Join-Path $frontendRoot 'package-lock.json'))) {
    throw 'frontend/package-lock.json is required for reproducible packaging.'
}
& npm.cmd --prefix $frontendRoot ci
if ($LASTEXITCODE -ne 0) { throw "npm ci failed for frontend workspace (exit code $LASTEXITCODE)." }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$stagingRoot = Join-Path $OutputRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
$tenantRoot = Join-Path $stagingRoot 'tenant'
$centerRoot = Join-Path $stagingRoot 'center'

try {
    foreach ($directory in @($tenantRoot, $centerRoot)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Publish-Project 'src/WorkerDock.Web/WorkerDock.Web.csproj' (Join-Path $tenantRoot 'web')
    Copy-Frontend '@workerdock/tenant' (Join-Path $tenantRoot 'web') (Join-Path $stagingRoot 'frontend/tenant-dist')
    Publish-Project 'src/WorkerDock.Scheduler/WorkerDock.Scheduler.csproj' (Join-Path $tenantRoot 'scheduler')
    Publish-Project 'src/WorkerDock.Worker/WorkerDock.Worker.csproj' (Join-Path $tenantRoot 'worker')
    Publish-Project 'src/WorkerDock.Executor/WorkerDock.Executor.csproj' (Join-Path $tenantRoot 'worker/executor')

    Publish-Project 'src/WorkerDock.DatabaseMigrator/WorkerDock.DatabaseMigrator.csproj' (Join-Path $tenantRoot 'tools/database-migrator')
    Publish-Project 'src/WorkerDock.ControlCenter/WorkerDock.ControlCenter.csproj' (Join-Path $centerRoot 'web')
    Copy-Frontend '@workerdock/center' (Join-Path $centerRoot 'web') (Join-Path $stagingRoot 'frontend/center-dist')
    Publish-Project 'src/WorkerDock.DatabaseMigrator/WorkerDock.DatabaseMigrator.csproj' (Join-Path $centerRoot 'tools/database-migrator')

    $tenantConfig = Get-RequiredPath 'deploy/config/tenant.example.json'
    $workerConfig = Get-RequiredPath 'deploy/config/worker.example.json'
    $centerConfig = Get-RequiredPath 'deploy/config/center.example.json'
    Copy-Item -LiteralPath $tenantConfig -Destination (Join-Path $tenantRoot 'tenant.example.json')
    Copy-Item -LiteralPath $workerConfig -Destination (Join-Path $tenantRoot 'worker.example.json')
    Copy-Item -LiteralPath $centerConfig -Destination (Join-Path $centerRoot 'center.example.json')

    foreach ($packageRoot in @($tenantRoot, $centerRoot)) {
        Copy-Item -LiteralPath (Get-RequiredPath 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
        Copy-Item -LiteralPath (Get-RequiredPath 'NOTICE') -Destination (Join-Path $packageRoot 'NOTICE')
        Copy-Item -LiteralPath (Get-RequiredPath 'docs/THIRD-PARTY-NOTICES-dotnet.md') -Destination (Join-Path $packageRoot 'THIRD-PARTY-NOTICES-dotnet.md')
    }

    Write-PackageManifest $tenantRoot 'tenant' $TenantSchemaMinVersion $TenantSchemaMaxVersion
    Write-PackageManifest $centerRoot 'center' $CenterSchemaMinVersion $CenterSchemaMaxVersion

    foreach ($kind in @('tenant', 'center')) {
        $archive = Join-Path $OutputRoot "workerdock-$kind-$Version.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path (Join-Path $stagingRoot $kind) '*') -DestinationPath $archive
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}
