[CmdletBinding()]
param(
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$deployRoot = (Resolve-Path (Join-Path $PSScriptRoot '../deploy')).Path
Import-Module (Join-Path $deployRoot 'WorkerDock.Deployment.psm1') -Force -DisableNameChecking

foreach ($script in Get-ChildItem -LiteralPath $deployRoot -Filter '*.ps1' -File) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw "PowerShell parse failed for $($script.Name): $($errors[0].Message)" }
}

$iisSiteFunction = (Get-Command New-HttpsIisSite).ScriptBlock.ToString()
if ($iisSiteFunction -notmatch '-PropertyName\s+sslFlags\s+-Value\s+1') {
    throw 'WorkerDock IIS HTTPS bindings must enable SNI for multiple tenant hostnames.'
}
$ensureServiceFunction = (Get-Command Ensure-Service).ScriptBlock.ToString()
$uninstallScript = Get-Content -LiteralPath (Join-Path $deployRoot 'Uninstall-Instance.ps1') -Raw
if ($ensureServiceFunction -notmatch 'Assert-OwnedService' -or $uninstallScript -notmatch 'Assert-OwnedService') {
    throw 'Install and uninstall must verify ownership before changing or removing Windows services.'
}

foreach ($configFile in Get-ChildItem -LiteralPath (Join-Path $deployRoot 'config') -Filter '*.json' -File) {
    $example = Get-Content -LiteralPath $configFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($configFile.Name -eq 'tenant.example.json' -and -not $example.Contains('Tenant')) { throw 'Tenant example does not use the Tenant host configuration section.' }
    if ($configFile.Name -eq 'worker.example.json' -and (-not $example.Contains('Tenant') -or -not $example.Contains('Worker') -or -not $example.Contains('Executor'))) {
        throw 'Worker example does not include Tenant, Worker, and Executor host configuration sections.'
    }
    if ($configFile.Name -eq 'center.example.json' -and -not $example.Contains('Central')) { throw 'Center example does not use the Central host configuration section.' }
}

if ($ConfigPath) {
    $config = Read-DeploymentConfig $ConfigPath
    if ($config.Contains('Tenant')) { [void](Assert-ConfigIdentity $config 'tenant' ([string]$config.Tenant.InstanceId)) }
    elseif ($config.Contains('Central')) { [void](Assert-ConfigIdentity $config 'center' ([string]$config.Central.InstanceId)) }
    else { throw 'ConfigPath must contain Tenant or Central host configuration.' }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WorkerDock-smoke-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ('WorkerDock-install-smoke-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    $ownedInstallRoot = Join-Path $scratch 'owned-install'
    $ownedExecutable = Join-Path $ownedInstallRoot 'releases/1.0.0/worker/WorkerDock.Worker.exe'
    $unownedExecutable = Join-Path $scratch 'other-service.exe'
    if (-not (Test-ServiceExecutableOwned $ownedExecutable $ownedInstallRoot)) { throw 'Owned service executable was not recognized under the installation release root.' }
    if (Test-ServiceExecutableOwned $unownedExecutable $ownedInstallRoot) { throw 'Unowned service executable was accepted under the installation release root.' }

    $backupSource = Join-Path $scratch 'release-source'
    [IO.Directory]::CreateDirectory($backupSource) | Out-Null
    $backupFile = Join-Path $backupSource 'worker.exe'
    Set-Content -LiteralPath $backupFile -Value 'application backup payload' -NoNewline
    $applicationArchive = Join-Path $scratch 'application.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($backupSource, $applicationArchive)
    if (-not (Assert-DirectoryArchive $applicationArchive $backupSource)) { throw 'Application archive verification did not report success.' }
    Set-Content -LiteralPath $backupFile -Value 'changed application payload' -NoNewline
    $rejected = $false
    try { $null = Assert-DirectoryArchive $applicationArchive $backupSource }
    catch { $rejected = $_.Exception.Message -match '(size|checksum) mismatch' }
    if (-not $rejected) { throw 'Application archive verification did not detect changed release content.' }

    $packageRoot = Join-Path $scratch 'package'
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $payload = Join-Path $packageRoot 'payload.txt'
    Set-Content -LiteralPath $payload -Value 'package integrity smoke' -NoNewline
    $hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        product = 'WorkerDock'
        packageKind = 'tenant'
        version = '0.0.0-smoke'
        schemaCompatibility = [ordered]@{ minimum = 1; maximum = 1 }
        files = @([ordered]@{ path = 'payload.txt'; sha256 = $hash; sizeBytes = (Get-Item -LiteralPath $payload).Length })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json')
    $verified = Get-VerifiedPackage $packageRoot 'tenant'
    if ($verified.Manifest.version -ne '0.0.0-smoke') { throw 'Verified package returned an unexpected version.' }
    $release = Install-VerifiedRelease $verified $installRoot
    if (-not (Test-Path -LiteralPath (Join-Path $release 'manifest.json') -PathType Leaf)) { throw 'Verified release did not install its manifest at the release root.' }
    Set-Content -LiteralPath (Join-Path $release 'payload.txt') -Value 'tampered installed release' -NoNewline
    $rejected = $false
    try { $null = Install-VerifiedRelease $verified $installRoot }
    catch { $rejected = $_.Exception.Message -match 'differs from its package manifest' }
    if (-not $rejected) { throw 'Installer accepted an altered file in an existing release directory.' }
    Close-VerifiedPackage $verified

    $manifest.version = '../escape'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json')
    $rejected = $false
    try { $null = Get-VerifiedPackage $packageRoot 'tenant' }
    catch { $rejected = $_.Exception.Message -match 'safe SemVer' }
    if (-not $rejected) { throw 'Manifest verifier accepted a path-traversal release version.' }

    $manifest.version = '0.0.0-smoke'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json')
    Set-Content -LiteralPath $payload -Value 'corrupt' -NoNewline
    $rejected = $false
    try { $null = Get-VerifiedPackage $packageRoot 'tenant' }
    catch { $rejected = $_.Exception.Message -match 'mismatch' }
    if (-not $rejected) { throw 'Manifest verifier did not reject a package with altered contents.' }
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
}

Write-Output 'pass'
