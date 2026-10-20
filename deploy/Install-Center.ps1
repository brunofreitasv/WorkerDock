[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid] $InstanceId,
    [Parameter(Mandatory)][string] $InstallRoot,
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $SiteName,
    [Parameter(Mandatory)][string] $HostName,
    [Parameter(Mandatory)][string] $CertificateThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerDock.Deployment.psm1') -Force
Assert-DeploymentHost
Assert-IisAvailable

$root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
$package = Get-VerifiedPackage $PackagePath 'center'
try {
    $configPathStored = Join-Path $root 'config/center.json'
    $incomingConfig = Read-DeploymentConfig $ConfigPath
    [void](Assert-ConfigIdentity $incomingConfig 'center' $InstanceId)
    $stateDirectory = Assert-CenterDataDirectory $incomingConfig $root
    $metadataPath = Join-Path $root 'config/deployment.json'
    $ownsIis = Test-Path -LiteralPath $metadataPath -PathType Leaf
    if ($ownsIis) {
        $existingMetadata = Read-DeploymentMetadata $root $InstanceId
        if ($existingMetadata.kind -ne 'center' -or $existingMetadata.siteName -ne $SiteName -or $existingMetadata.hostName -ne $HostName -or
            $existingMetadata.certificateThumbprint -ne $CertificateThumbprint.Replace(' ','').ToUpperInvariant()) {
            throw 'Existing center installation identities or IIS binding differ; refusing to replace them.'
        }
        if ([string]$existingMetadata.activeVersion -ne [string]$package.Manifest.version) { throw 'A different package version is installed. Use Upgrade-Instance.' }
        if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {
            Write-Output "Center instance $InstanceId is already installed at version $($package.Manifest.version)."; return
        }
    }
    $config = Save-PersistentConfig $ConfigPath $configPathStored $InstanceId 'center'
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    $poolName = "WorkerDock-$($InstanceId.ToString('N').Substring(0,8))-Center"
    Protect-SensitiveDirectory $stateDirectory "IIS AppPool\$poolName"
    $release = Install-VerifiedRelease $package $root
    Set-ReleaseConfig $configPathStored (Join-Path $release 'web/appsettings.Production.json')
    Write-DeploymentMetadata $root ([ordered]@{
        kind = 'center'; instanceId = $InstanceId.ToString(); activeVersion = [string]$package.Manifest.version
        siteName = $SiteName; poolName = $poolName
        hostName = $HostName; certificateThumbprint = $CertificateThumbprint.Replace(' ','').ToUpperInvariant(); stateDirectory = $stateDirectory
    })
    New-HttpsIisSite $SiteName $poolName $HostName $CertificateThumbprint (Join-Path $release 'web') -Owned:$ownsIis
    Protect-ConfigDirectory (Join-Path $root 'config') @("IIS AppPool\$poolName")
    Protect-SensitiveDirectory $stateDirectory "IIS AppPool\$poolName"
} finally { Close-VerifiedPackage $package }
