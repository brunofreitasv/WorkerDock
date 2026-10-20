$ErrorActionPreference = 'Stop'
$module = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'deploy/WorkerDock.Deployment.psm1'
Import-Module $module -Force -DisableNameChecking
function Assert-True([bool] $Value, [string] $Message) { if (-not $Value) { throw $Message } }
function Assert-Throws([scriptblock] $Action, [string] $Message) { try { & $Action } catch { return }; throw $Message }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WorkerDock-center-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $root = Join-Path $temp 'install'; $outsideState = Join-Path $temp 'state'; $guid = [guid]::NewGuid().ToString()
    $config = @{ Central = @{ InstanceId = $guid; Storage = @{ DataDirectory = $outsideState } } }
    Assert-True ((Assert-ConfigIdentity $config 'center' $guid) -eq 'Central') 'Center config without SQL connection string was rejected.'
    Assert-True ((Assert-CenterDataDirectory $config $root) -eq [IO.Path]::GetFullPath($outsideState)) 'Absolute center state directory was not accepted.'
    Assert-Throws { Assert-CenterDataDirectory @{ Central = @{ Storage = @{ DataDirectory = (Join-Path $root 'releases/v1') } } } $root } 'Release state path was accepted.'
    Assert-Throws { Assert-CenterDataDirectory @{ Central = @{ Storage = @{ DataDirectory = (Join-Path $root 'releases/v1/web') } } } $root } 'Web-root state path was accepted.'
    $centerPackageRoot = Join-Path $temp 'package-center'; [IO.Directory]::CreateDirectory((Join-Path $centerPackageRoot 'web')) | Out-Null
    'web' | Set-Content (Join-Path $centerPackageRoot 'web/app.dll'); $centerFile = Get-Item (Join-Path $centerPackageRoot 'web/app.dll')
    @{ product = 'WorkerDock'; packageKind = 'center'; version = '1.2.3'; components = @{ web = '1.2.3' }; stateFormatCompatibility = @{ minimum = 1; maximum = 1 }; files = @(@{ path = 'web/app.dll'; sha256 = (Get-FileHash $centerFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = $centerFile.Length }) } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $centerPackageRoot 'manifest.json')
    $verifiedCenter = Get-VerifiedPackage $centerPackageRoot 'center'
    Assert-True ($verifiedCenter.Manifest.stateFormatCompatibility.minimum -eq 1) 'Center state format manifest was not accepted.'
    Assert-True (-not $verifiedCenter.Manifest.Contains('schemaCompatibility')) 'Center package retained schema compatibility.'
    Assert-True (-not $verifiedCenter.Manifest.components.Contains('migrator')) 'Center package retained a migrator component.'
    $tenantRoot = Join-Path $temp 'package-tenant'; [IO.Directory]::CreateDirectory($tenantRoot) | Out-Null
    @{ product = 'WorkerDock'; packageKind = 'tenant'; version = '1.2.3'; files = @(@{ path = 'missing.dll'; sha256 = ('0' * 64); sizeBytes = 1 }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $tenantRoot 'manifest.json')
    Assert-Throws { Get-VerifiedPackage $tenantRoot 'tenant' } 'Tenant manifest without schema data was accepted.'
    @{ product = 'WorkerDock'; packageKind = 'tenant'; version = '1.2.3'; schemaCompatibility = @{ minimum = 0; maximum = 8 }; files = @(@{ path = 'missing.dll'; sha256 = ('0' * 64); sizeBytes = 1 }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $tenantRoot 'manifest.json')
    Assert-Throws { Get-VerifiedPackage $tenantRoot 'tenant' } 'Mismatched tenant schema data was accepted.'
    $state = Join-Path $temp 'state-data'; $release = Join-Path $temp 'release'; $configPath = Join-Path $temp 'center.json'
    [IO.Directory]::CreateDirectory($state) | Out-Null; [IO.Directory]::CreateDirectory($release) | Out-Null
    'journal-state' | Set-Content (Join-Path $state 'registry.journal'); 'health-state' | Set-Content (Join-Path $state 'health.json')
    @{ Central = @{ InstanceId = $guid; Storage = @{ DataDirectory = $state } } } | ConvertTo-Json -Depth 5 | Set-Content $configPath; 'previous-release' | Set-Content (Join-Path $release 'WorkerDock.ControlCenter.dll')
    $archivePath = Join-Path $temp 'center-backup.zip'; New-CenterBackupArchive $state $configPath $release $archivePath $guid '1.2.2' | Out-Null
    $backupManifest = Assert-CenterBackupArchive $archivePath
    Assert-True ($backupManifest.instanceId -eq $guid -and $backupManifest.releaseVersion -eq '1.2.2') 'Backup did not record the center identity and previous release version.'
    $restore = Join-Path $temp 'restored'; $null = Restore-CenterBackupArchive $archivePath $restore
    foreach ($relative in @('state/registry.journal','state/health.json','config/center.json','release/WorkerDock.ControlCenter.dll')) {
        $source = Join-Path $restore $relative; Assert-True (Test-Path -LiteralPath $source -PathType Leaf) "Backup restore omitted $relative."
        $archiveEntry = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try { $entry = $archiveEntry.GetEntry($relative); $hashStream = [Security.Cryptography.SHA256]::Create(); try { $stream = $entry.Open(); try { $hash = [Convert]::ToHexString($hashStream.ComputeHash($stream)).ToLowerInvariant() } finally { $stream.Dispose() } } finally { $hashStream.Dispose() }; Assert-True ($hash -eq (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()) "Restored hash mismatch for $relative." } finally { $archiveEntry.Dispose() }
    }
    Write-Output 'pass'
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }

