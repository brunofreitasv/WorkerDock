Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-DeploymentHost {
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
    if (-not $IsWindows) { throw 'Windows is required for WorkerDock deployment.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this deployment operation from an elevated PowerShell session.'
    }
}

function Resolve-DeploymentPath([string] $Path, [string] $Name) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "$Name must be an absolute path." }
    return [IO.Path]::GetFullPath($Path)
}

function Read-DeploymentConfig([string] $Path) {
    $fullPath = Resolve-DeploymentPath $Path 'ConfigPath'
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Configuration file not found: $fullPath" }
    $config = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json -AsHashtable
    if ($null -eq $config -or $config -isnot [System.Collections.IDictionary]) { throw 'Configuration must be a JSON object.' }
    $placeholders = [Collections.Generic.List[string]]::new()
    $visit = {
        param($Value, [string] $PathName)
        if ($Value -is [System.Collections.IDictionary]) {
            foreach ($key in $Value.Keys) { & $visit $Value[$key] "$PathName.$key" }
        } elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
            $index = 0
            foreach ($item in $Value) { & $visit $item "$PathName[$index]"; $index++ }
        } elseif ($Value -is [string] -and $Value -match '<[^>]+>') {
            $placeholders.Add($PathName)
        }
    }
    & $visit $config 'root'
    if ($placeholders.Count) { throw "Configuration contains unresolved placeholders: $($placeholders -join ', ')." }
    return $config
}

function Get-ConfigValue($Config, [string] $Section, [string] $Name) {
    if ($Config.Contains($Section) -and $Config[$Section] -is [System.Collections.IDictionary] -and $Config[$Section].Contains($Name)) {
        return $Config[$Section][$Name]
    }
    return $null
}

function Assert-ConfigIdentity($Config, [ValidateSet('tenant','center')] [string] $Kind, [string] $InstanceId) {
    $section = if ($Kind -eq 'tenant') { 'Tenant' } else { 'Central' }
    $configuredId = Get-ConfigValue $Config $section 'InstanceId'
    $configuredConnection = Get-ConfigValue $Config $section 'ConnectionString'
    $parsedId = [guid]::Empty
    if (-not [guid]::TryParse([string]$configuredId, [ref]$parsedId)) { throw "$section.InstanceId must be a GUID." }
    if ([string]$configuredId -ne $InstanceId) { throw "Configuration instance identity does not match requested instance $InstanceId." }
    if ($Kind -eq 'tenant' -and [string]::IsNullOrWhiteSpace([string]$configuredConnection)) { throw "$section.ConnectionString is required." }
    if ($Kind -eq 'center') {
        $central = $Config['Central']
        $dataDirectory = if ($central -is [System.Collections.IDictionary] -and $central['Storage'] -is [System.Collections.IDictionary]) { $central['Storage']['DataDirectory'] } else { $null }
        if ([string]::IsNullOrWhiteSpace([string]$dataDirectory) -or -not [IO.Path]::IsPathFullyQualified([string]$dataDirectory)) { throw 'Central.Storage.DataDirectory must be an absolute path.' }
    }
    return $section
}

function Assert-CenterDataDirectory($Config, [string] $InstallRoot) {
    $central = $Config['Central']
    $value = if ($central -is [System.Collections.IDictionary] -and $central['Storage'] -is [System.Collections.IDictionary]) { $central['Storage']['DataDirectory'] } else { $null }
    if ([string]::IsNullOrWhiteSpace([string]$value) -or -not [IO.Path]::IsPathFullyQualified([string]$value)) {
        throw 'Central.Storage.DataDirectory must be an absolute path.'
    }
    $path = [IO.Path]::GetFullPath([string]$value).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $webRoot = [IO.Path]::GetFullPath((Join-Path $root 'wwwroot')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    foreach ($protectedRoot in @($root, $webRoot)) {
        if ($path.Equals($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($protectedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Central.Storage.DataDirectory must be outside the install, release, and web directories.'
        }
    }
    return $path
}

function Get-VerifiedPackage([string] $PackagePath, [ValidateSet('tenant','center')] [string] $ExpectedKind) {
    $fullPath = Resolve-DeploymentPath $PackagePath 'PackagePath'
    $temporaryRoot = $null
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        if ([IO.Path]::GetExtension($fullPath) -ne '.zip') { throw 'PackagePath must be a WorkerDock ZIP archive or unpacked package directory.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('WorkerDock-package-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($fullPath, $temporaryRoot)
        $root = $temporaryRoot
    } elseif (Test-Path -LiteralPath $fullPath -PathType Container) {
        $root = $fullPath
    } else { throw "Package not found: $fullPath" }

    try {
        $manifestPath = Join-Path $root 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package manifest.json is missing.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        if ($manifest.product -ne 'WorkerDock' -or $manifest.packageKind -ne $ExpectedKind) { throw "Expected a WorkerDock $ExpectedKind package." }
        if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
            throw 'Package manifest version must be a safe SemVer-style version.'
        }
        if ($ExpectedKind -eq 'center') {
            if ($null -eq $manifest.stateFormatCompatibility -or [int]$manifest.stateFormatCompatibility.minimum -lt 1 -or
                [int]$manifest.stateFormatCompatibility.maximum -lt [int]$manifest.stateFormatCompatibility.minimum) { throw 'Package stateFormatCompatibility range is invalid.' }
            if (-not $manifest.Contains('components') -or $manifest.components.Count -ne 1 -or -not $manifest.components.Contains('web')) { throw 'Center package must contain the web component only.' }
            if (($manifest.Contains('components') -and $manifest.components.Contains('migrator')) -or
                ($manifest.Contains('layout') -and $manifest.layout.Contains('migrator'))) { throw 'Center package must not contain a database migrator.' }
        } elseif ($null -eq $manifest.schemaCompatibility -or [int]$manifest.schemaCompatibility.minimum -lt 1 -or
            [int]$manifest.schemaCompatibility.maximum -lt [int]$manifest.schemaCompatibility.minimum) { throw 'Package schemaCompatibility range is invalid.' }
        if ($null -eq $manifest.files -or $manifest.files.Count -eq 0) { throw 'Package manifest file list is empty.' }

        $listed = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $manifest.files) {
            $relative = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains(':') -or $relative.StartsWith('/') -or
                $relative.Split('/') -contains '..' -or $relative.Contains('\')) { throw "Unsafe manifest path: $relative" }
            if ($listed.ContainsKey($relative)) { throw "Duplicate manifest path: $relative" }
            $listed.Add($relative, $entry)
        }
        $actualFiles = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.FullName -ne $manifestPath })
        if ($actualFiles.Count -ne $listed.Count) { throw 'Package contents do not match the manifest file list.' }
        foreach ($file in $actualFiles) {
            $relative = $file.FullName.Substring($root.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
            if (-not $listed.ContainsKey($relative)) { throw "Unlisted package file: $relative" }
            $entry = $listed[$relative]
            if ($file.Length -ne [long]$entry.sizeBytes) { throw "Package size mismatch: $relative" }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Package checksum mismatch: $relative" }
        }
        [pscustomobject]@{ Root = $root; TemporaryRoot = $temporaryRoot; Manifest = $manifest }
    } catch {
        if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
        throw
    }
}

function Close-VerifiedPackage($Package) {
    if ($Package.TemporaryRoot -and (Test-Path -LiteralPath $Package.TemporaryRoot)) {
        Remove-Item -LiteralPath $Package.TemporaryRoot -Recurse -Force
    }
}

function Assert-DirectoryArchive([string] $ArchivePath, [string] $SourceDirectory, [string] $ExcludeFilePattern = 'appsettings*.json') {
    $archiveFullPath = Resolve-DeploymentPath $ArchivePath 'ArchivePath'
    $sourceRoot = Resolve-DeploymentPath $SourceDirectory 'SourceDirectory'
    $expected = [Collections.Generic.Dictionary[string,IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object { $_.Name -notlike $ExcludeFilePattern }) {
        $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
        if ($expected.ContainsKey($relative)) { throw "Duplicate source path in application backup: $relative" }
        $expected.Add($relative, $file)
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($archiveFullPath)
    try {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($entries.Count -ne $expected.Count) { throw 'Application backup entry count differs from the release directory.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            foreach ($entry in $entries) {
                $relative = $entry.FullName.Replace('\','/').TrimStart('/')
                if (-not $expected.ContainsKey($relative) -or -not $seen.Add($relative)) { throw "Unexpected or duplicate application backup entry: $relative" }
                $source = $expected[$relative]
                if ($entry.Length -ne $source.Length) { throw "Application backup size mismatch: $relative" }
                $entryStream = $entry.Open()
                try { $archiveHash = [Convert]::ToHexString($hasher.ComputeHash($entryStream)).ToLowerInvariant() }
                finally { $entryStream.Dispose() }
                $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($archiveHash -ne $sourceHash) { throw "Application backup checksum mismatch: $relative" }
            }
        } finally { $hasher.Dispose() }
    } finally { $archive.Dispose() }
    return $true
}

function New-CenterBackupArchive([string] $StateDirectory, [string] $ConfigPath, [string] $ReleaseDirectory, [string] $BackupPath, [guid] $InstanceId, [string] $ReleaseVersion) {
    $archivePath = Resolve-DeploymentPath $BackupPath 'BackupPath'
    if ([IO.Path]::GetExtension($archivePath) -ne '.zip') { throw 'Center BackupPath must use the .zip extension.' }
    if (Test-Path -LiteralPath $archivePath) { throw 'BackupPath already exists; refusing to overwrite a center backup.' }
    $state = Resolve-DeploymentPath $StateDirectory 'StateDirectory'
    $config = Resolve-DeploymentPath $ConfigPath 'ConfigPath'
    $release = Resolve-DeploymentPath $ReleaseDirectory 'ReleaseDirectory'
    if ($InstanceId -eq [guid]::Empty -or $ReleaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Center backup requires a valid instance ID and previous release version.' }
    foreach ($required in @($state,$config,$release)) { if (-not (Test-Path -LiteralPath $required)) { throw "Center backup input is missing: $required" } }
    if (-not (Test-Path -LiteralPath (Join-Path $state 'registry.journal') -PathType Leaf)) { throw 'Center state directory has no registry.journal to back up.' }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $archivePath)) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = [Collections.Generic.List[object]]::new()
        foreach ($source in Get-ChildItem -LiteralPath $state -File -Recurse) {
            $relative = $source.FullName.Substring($state.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
            $entryName = "state/$relative"
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            $files.Add([ordered]@{ path = $entryName; sha256 = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = $source.Length })
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $config, 'config/center.json', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $files.Add([ordered]@{ path = 'config/center.json'; sha256 = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = (Get-Item -LiteralPath $config).Length })
        foreach ($source in Get-ChildItem -LiteralPath $release -File -Recurse) {
            $relative = $source.FullName.Substring($release.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
            $entryName = "release/$relative"
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            $files.Add([ordered]@{ path = $entryName; sha256 = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = $source.Length })
        }
        $manifest = [ordered]@{ format = 1; instanceId = $InstanceId; releaseVersion = $ReleaseVersion; createdUtc = [DateTimeOffset]::UtcNow.ToString('O'); files = @($files) }
        $entry = $zip.CreateEntry('manifest.json')
        $stream = $entry.Open()
        try { $writer = [IO.StreamWriter]::new($stream); try { $writer.Write(($manifest | ConvertTo-Json -Depth 8)) } finally { $writer.Dispose() } } finally { $stream.Dispose() }
    } catch { $zip.Dispose(); Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue; throw }
    $zip.Dispose()
    [void](Assert-CenterBackupArchive $archivePath)
    return $archivePath
}

function Assert-CenterBackupArchive([string] $ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-DeploymentPath $ArchivePath 'BackupPath'))
    try {
        $manifestEntry = $archive.GetEntry('manifest.json')
        if (-not $manifestEntry) { throw 'Center backup manifest is missing.' }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
        if ($manifest.format -ne 1 -or $manifest.files.Count -eq 0) { throw 'Center backup manifest format is invalid.' }
        $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $manifest.files) {
            $path = [string]$file.path
            if (-not $path -or $path.Contains('\') -or $path.Contains(':') -or $path.StartsWith('/') -or $path.Split('/') -contains '..' -or
                -not ($path.StartsWith('state/', [StringComparison]::Ordinal) -or $path -eq 'config/center.json' -or $path.StartsWith('release/', [StringComparison]::Ordinal)) -or -not $listed.Add($path)) {
                throw "Unsafe or duplicate center backup path: $path"
            }
            $entry = $archive.GetEntry($path)
            if (-not $entry -or $entry.Length -ne [long]$file.sizeBytes) { throw "Center backup entry is missing or has wrong size: $path" }
            $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($hasher.ComputeHash($stream)).ToLowerInvariant() } finally { $stream.Dispose(); $hasher.Dispose() }
            if ($hash -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Center backup checksum mismatch: $path" }
        }
        if (-not $listed.Contains('state/registry.journal') -or -not $listed.Contains('config/center.json') -or
            -not @($manifest.files | Where-Object { ([string]$_.path).StartsWith('release/', [StringComparison]::Ordinal) }).Count) {
            throw 'Center backup must contain the journal, configuration, and previous release.'
        }
        if ($archive.Entries.Count -ne $listed.Count + 1) { throw 'Center backup contains unverified entries.' }
        return $manifest
    } finally { $archive.Dispose() }
}

function Restore-CenterBackupArchive([string] $ArchivePath, [string] $Destination) {
    $manifest = Assert-CenterBackupArchive $ArchivePath
    $archiveForIdentity = [IO.Compression.ZipFile]::OpenRead((Resolve-DeploymentPath $ArchivePath 'BackupPath'))
    try {
        $configEntry = $archiveForIdentity.GetEntry('config/center.json')
        $reader = [IO.StreamReader]::new($configEntry.Open())
        try { $centerConfig = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
        if (-not $manifest.instanceId -or [string]$centerConfig.Central.InstanceId -ne [string]$manifest.instanceId) { throw 'Center backup configuration identity does not match its manifest.' }
    } finally { $archiveForIdentity.Dispose() }
    $root = Resolve-DeploymentPath $Destination 'RestoreDestination'
    if (Test-Path -LiteralPath $root) {
        if (@(Get-ChildItem -LiteralPath $root -Force).Count -gt 0) { throw 'Center restore destination must be empty.' }
    } else { [IO.Directory]::CreateDirectory($root) | Out-Null }
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-DeploymentPath $ArchivePath 'BackupPath'))
    try {
        foreach ($file in $manifest.files) {
            $target = Join-Path $root ([string]$file.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            $source = $archive.GetEntry([string]$file.path).Open(); $destinationStream = [IO.File]::Create($target)
            try { $source.CopyTo($destinationStream) } finally { $destinationStream.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
    return $root
}

function Install-VerifiedRelease($Package, [string] $InstallRoot) {
    $root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
    $releases = Join-Path $root 'releases'
    $release = Join-Path $releases ([string]$Package.Manifest.version)
    [IO.Directory]::CreateDirectory($releases) | Out-Null
    if (Test-Path -LiteralPath $release) {
        $existingManifest = Join-Path $release 'manifest.json'
        if (-not (Test-Path -LiteralPath $existingManifest -PathType Leaf) -or
            (Get-FileHash -LiteralPath $existingManifest -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Package.Root 'manifest.json') -Algorithm SHA256).Hash) {
            throw "Release directory already exists with different content: $release"
        }
        $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $Package.Manifest.files) { $expected[[string]$entry.path] = $entry }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse | Where-Object { $_.FullName -ne $existingManifest }) {
            $relative = $file.FullName.Substring($release.Length).TrimStart([char[]]@('\','/')).Replace('\','/')
            if ($expected.ContainsKey($relative)) {
                $entry = $expected[$relative]
                if ($file.Length -ne [long]$entry.sizeBytes -or
                    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) {
                    throw "Existing release file differs from its package manifest: $relative"
                }
                [void]$seen.Add($relative)
            } elseif ($file.Name -notmatch '^appsettings(?:\..+)?\.json$') {
                throw "Existing release contains an unlisted file: $relative"
            }
        }
        if ($seen.Count -ne $expected.Count) { throw 'Existing release is missing files listed in its package manifest.' }
        return $release
    }
    Copy-Item -LiteralPath $Package.Root -Destination $release -Recurse | Out-Null
    return $release
}

function Save-PersistentConfig([string] $SourcePath, [string] $DestinationPath, [string] $InstanceId, [ValidateSet('tenant','center')] [string] $Kind) {
    $incoming = Read-DeploymentConfig $SourcePath
    [void](Assert-ConfigIdentity $incoming $Kind $InstanceId)
    if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
        $existing = Read-DeploymentConfig $DestinationPath
        [void](Assert-ConfigIdentity $existing $Kind $InstanceId)
        return $existing
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $DestinationPath)) | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    return (Read-DeploymentConfig $DestinationPath)
}

function Save-WorkerConfig([string] $SourcePath, [string] $DestinationPath, [string] $InstanceId, [string] $ExecutorPath) {
    $fullSource = Resolve-DeploymentPath $SourcePath 'WorkerConfigPath'
    $incoming = Get-Content -LiteralPath $fullSource -Raw | ConvertFrom-Json -AsHashtable
    if ($ExecutorPath) { $incoming.Executor.Path = $ExecutorPath }
    $installationId = [guid]::Empty
    if ([string]$incoming.Tenant.InstanceId -ne $InstanceId -or -not [guid]::TryParse([string]$incoming.Worker.InstallationId, [ref]$installationId) -or
        [string]$incoming.Worker.CatalogDigest -notmatch '^[a-fA-F0-9]{64}$' -or [int]$incoming.Worker.HostCeiling -lt 1 -or
        [int]$incoming.Worker.DrainSeconds -lt 1 -or [string]::IsNullOrWhiteSpace([string]$incoming.Tenant.ConnectionString)) {
        throw 'Worker configuration must contain matching tenant identity, installation identity, catalog digest, positive limits, and connection string.'
    }
    if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
        $existing = Read-DeploymentConfig $DestinationPath
        if ([string]$existing.Tenant.InstanceId -ne $InstanceId -or [string]$existing.Worker.InstallationId -ne [string]$incoming.Worker.InstallationId) {
            throw 'Existing worker configuration has a different tenant or installation identity; it was preserved.'
        }
        return $existing
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $DestinationPath)) | Out-Null
    [IO.Directory]::CreateDirectory((Split-Path -Parent $DestinationPath)) | Out-Null
    $incoming | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $DestinationPath -Encoding utf8
    return (Read-DeploymentConfig $DestinationPath)
}

function Set-ReleaseConfig([string] $SourcePath, [string] $TargetPath, [string] $ExecutorPath = $null) {
    $config = Read-DeploymentConfig $SourcePath
    if ($ExecutorPath) { $config.Executor.Path = $ExecutorPath }
    $json = ConvertTo-Json -InputObject $config -Depth 30
    Set-Content -LiteralPath $TargetPath -Value $json -Encoding utf8
}

function Assert-IisAvailable {
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) { throw 'IIS WebAdministration PowerShell module is unavailable.' }
    Import-Module WebAdministration -ErrorAction Stop
}

function Assert-HttpsCertificate([string] $Thumbprint) {
    $normalized = $Thumbprint.Replace(' ','').ToUpperInvariant()
    if ($normalized -notmatch '^[A-F0-9]{40}$') { throw 'A valid LocalMachine\My HTTPS certificate thumbprint is required.' }
    if (-not (Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $normalized)) { throw "HTTPS certificate not found in LocalMachine\My: $normalized" }
    return $normalized
}

function Set-ServiceEnvironment([string] $Name, [string] $Environment) {
    if ($Environment -notmatch '^[A-Za-z0-9_.-]{1,100}$') { throw 'Service environment name is invalid.' }
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $current = @((Get-ItemProperty -LiteralPath $key -Name Environment -ErrorAction SilentlyContinue).Environment)
    $current = @($current | Where-Object { $_ -notmatch '^DOTNET_ENVIRONMENT=' })
    $next = @($current + "DOTNET_ENVIRONMENT=$Environment")
    New-ItemProperty -LiteralPath $key -Name Environment -PropertyType MultiString -Value $next -Force | Out-Null
}

function Protect-ConfigDirectory([string] $Path, [string[]] $ReadIdentities) {
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    & icacls.exe $Path '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)(F)' 'BUILTIN\Administrators:(OI)(CI)(F)' '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to configuration directory: $Path" }
    foreach ($identity in @($ReadIdentities | Where-Object { $_ } | Select-Object -Unique)) {
        & icacls.exe $Path '/grant' "$($identity):(OI)(CI)(RX)" '/T' '/C' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not grant configuration read access to $identity." }
    }
}

function Grant-DirectoryAccess([string] $Path, [string] $Identity, [ValidateSet('RX','M')][string] $Rights) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "ACL target directory is missing: $Path" }
    & icacls.exe $Path '/grant' "$($Identity):(OI)(CI)($Rights)" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant $Rights access to $Identity on $Path." }
}

function Protect-SensitiveDirectory([string] $Path, [string] $Identity) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "Sensitive directory must be an absolute path: $Path" }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    & icacls.exe $Path '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)(F)' 'BUILTIN\Administrators:(OI)(CI)(F)' "$($Identity):(OI)(CI)(M)" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to sensitive directory: $Path" }
}

function Assert-OwnedService([string] $Name, [string] $InstallRoot, [string] $ExpectedAccount = $null) {
    $root = Resolve-DeploymentPath $InstallRoot 'InstallRoot'
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop
    if (-not $service) { throw "Windows service is missing: $Name" }
    $imagePath = [string]$service.PathName
    if ($imagePath -match '^\s*"([^"]+)"') { $executable = $Matches[1] }
    else { $executable = ($imagePath -split '\s+', 2)[0] }
    if (-not [IO.Path]::IsPathFullyQualified($executable)) { throw "Windows service $Name has an invalid executable path." }
    $releaseRoot = Join-Path $root 'releases'
    $releasePrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullExecutable = [IO.Path]::GetFullPath($executable)
    if (-not $fullExecutable.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Windows service $Name does not point inside this WorkerDock installation's releases directory."
    }
    if ($ExpectedAccount -and [string]$service.StartName -ine $ExpectedAccount) {
        throw "Windows service $Name is configured with a different account than this WorkerDock installation."
    }
}

function Test-ServiceExecutableOwned([string] $Executable, [string] $InstallRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($Executable) -or -not [IO.Path]::IsPathFullyQualified($InstallRoot)) { return $false }
    $root = [IO.Path]::GetFullPath($InstallRoot)
    $releasePrefix = [IO.Path]::GetFullPath((Join-Path $root 'releases')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Executable).StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Ensure-Service([string] $Name, [string] $Executable, [string] $Account, [string] $Environment = 'Production', [string] $InstallRoot) {
    if ($Name -notmatch '^[A-Za-z0-9_.-]{1,80}$') { throw "Invalid Windows service name: $Name" }
    $isVirtual = $Account -eq "NT SERVICE\$Name"
    $isBuiltIn = $Account -in @('NT AUTHORITY\LocalService','NT AUTHORITY\NetworkService')
    $isGmsa = $Account -match '^[A-Za-z0-9_.-]+\\[A-Za-z0-9_.-]+\$$'
    if (-not ($isVirtual -or $isBuiltIn -or $isGmsa)) {
        throw 'ServiceAccount must be LocalService, NetworkService, the service virtual account, or a gMSA ending in $. Ordinary password-based accounts are not accepted.'
    }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Service executable is missing: $Executable" }
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') { throw "Service $Name must be stopped before installing or changing its binary path." }
        if (-not $InstallRoot) { throw 'InstallRoot is required to verify an existing Windows service before updating it.' }
        Assert-OwnedService $Name $InstallRoot $Account
        $result = & sc.exe config $Name "binPath=`"$Executable`"" "obj=$Account" 'start=auto'
        if ($LASTEXITCODE -ne 0) { throw "Could not update Windows service $($Name): $result" }
    } else {
        $result = & sc.exe create $Name "binPath=`"$Executable`"" "obj=$Account" 'start=auto' 'DisplayName=WorkerDock service'
        if ($LASTEXITCODE -ne 0) { throw "Could not create Windows service $($Name): $result" }
    }
    & sc.exe sidtype $Name restricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict the service SID for $Name." }
    & sc.exe failure $Name 'reset=86400' 'actions=restart/60000/restart/60000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure service recovery for $Name." }
    $aclIdentity = if ($Account -like 'NT SERVICE\*') { $Account } else { $Account }
    & icacls.exe (Split-Path -Parent $Executable) '/grant' "$aclIdentity`:(OI)(CI)(RX)" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant service $Name read access to its release." }
    Set-ServiceEnvironment $Name $Environment
}

function New-HttpsIisSite([string] $SiteName, [string] $PoolName, [string] $HostName, [string] $Thumbprint, [string] $PhysicalPath, [switch] $Owned) {
    Assert-IisAvailable
    $thumb = Assert-HttpsCertificate $Thumbprint
    if ($SiteName -notmatch '^[A-Za-z0-9_.-]{1,80}$' -or $PoolName -notmatch '^[A-Za-z0-9_.-]{1,80}$') { throw 'IIS site and application pool names may contain only letters, digits, dot, underscore, and hyphen.' }
    if ($HostName -notmatch '^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$') { throw 'HostName must be a DNS host name.' }
    $existingSite = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    $poolExists = Test-Path -LiteralPath "IIS:\AppPools\$PoolName"
    if ($existingSite -and -not $Owned) { throw "IIS site already exists and is not owned by this installation metadata: $SiteName" }
    if ($poolExists -and -not $Owned) { throw "IIS application pool already exists and is not owned by this installation metadata: $PoolName" }
    if ($existingSite) {
        if ($existingSite.applicationPool -ne $PoolName) { throw 'Owned IIS site references a different application pool.' }
        $expectedPath = [IO.Path]::GetFullPath($PhysicalPath).TrimEnd('\\')
        if ([IO.Path]::GetFullPath([string]$existingSite.physicalPath).TrimEnd('\\') -ne $expectedPath) {
            throw 'IIS site points at another release. Use Upgrade-Instance to change active versions.'
        }
        $expectedBinding = "*:443:$HostName"
        if (-not ((Get-WebBinding -Name $SiteName -Protocol https).bindingInformation -contains $expectedBinding)) {
            throw 'Owned IIS site HTTPS binding differs from the requested host name.'
        }
    } else {
        if (-not $poolExists) { New-WebAppPool -Name $PoolName | Out-Null }
        Set-ItemProperty "IIS:\AppPools\$PoolName" -Name managedRuntimeVersion -Value ''
        Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.identityType -Value 4
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName -IPAddress '*' -Port 443 -HostHeader $HostName -Protocol https | Out-Null
    }
    $expectedBinding = "*:443:$HostName"
    $binding = Get-WebBinding -Name $SiteName -Protocol https | Where-Object bindingInformation -eq $expectedBinding
    if (-not $binding) { throw 'The expected HTTPS host binding is missing from the WorkerDock IIS site.' }
    Set-WebBinding -Name $SiteName -BindingInformation $expectedBinding -PropertyName sslFlags -Value 1
    $binding = Get-WebBinding -Name $SiteName -Protocol https | Where-Object bindingInformation -eq $expectedBinding
    if ([int]$binding.sslFlags -ne 1) { throw 'WorkerDock HTTPS host binding must use SNI for multi-tenant hosting.' }
    $certificateHash = if ($binding.CertificateHash) { [Convert]::ToHexString([byte[]]$binding.CertificateHash) } else { '' }
    if ($certificateHash -ne $thumb) { $binding.AddSslCertificate($thumb, 'my') }
    if ((Get-Website -Name $SiteName).State -ne 'Stopped') { Stop-Website -Name $SiteName }
    & icacls.exe $PhysicalPath '/grant' "IIS AppPool\$PoolName`:(OI)(CI)(RX)" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant IIS pool $PoolName read access to its release." }
    $keyDirectory = if (Test-Path -LiteralPath (Join-Path $PhysicalPath 'appsettings.Production.json')) {
        (Get-Content (Join-Path $PhysicalPath 'appsettings.Production.json') -Raw | ConvertFrom-Json).DataProtection.KeyDirectory
    }
    if ($keyDirectory) {
        Protect-SensitiveDirectory ([string]$keyDirectory) "IIS AppPool\$PoolName"
    }
}

function Write-DeploymentMetadata([string] $InstallRoot, $Metadata) {
    $configRoot = Join-Path $InstallRoot 'config'
    [IO.Directory]::CreateDirectory($configRoot) | Out-Null
    $path = Join-Path $configRoot 'deployment.json'
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $existing = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in @('kind','instanceId','siteName','poolName','schedulerServiceName','workerServiceName')) {
            if ($Metadata.ContainsKey($key) -and $existing.ContainsKey($key) -and $existing[$key] -ne $Metadata[$key]) {
                throw "Existing deployment metadata identity differs at $key; refusing to replace it."
            }
        }
        return
    }
    $Metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8
}

function Read-DeploymentMetadata([string] $InstallRoot, [string] $InstanceId) {
    $path = Join-Path (Resolve-DeploymentPath $InstallRoot 'InstallRoot') 'config/deployment.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "WorkerDock installation metadata is missing: $path" }
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ([string]$metadata.instanceId -ne $InstanceId) { throw 'InstallRoot belongs to a different WorkerDock instance.' }
    return $metadata
}

Export-ModuleMember -Function Assert-DeploymentHost, Resolve-DeploymentPath, Read-DeploymentConfig, Get-ConfigValue, Assert-ConfigIdentity,
    Assert-CenterDataDirectory, Get-VerifiedPackage, Close-VerifiedPackage, Assert-DirectoryArchive, New-CenterBackupArchive, Assert-CenterBackupArchive, Restore-CenterBackupArchive, Install-VerifiedRelease, Save-PersistentConfig, Save-WorkerConfig, Set-ReleaseConfig,
    Assert-IisAvailable, Assert-HttpsCertificate, Set-ServiceEnvironment, Protect-ConfigDirectory, Grant-DirectoryAccess,
    Protect-SensitiveDirectory, Ensure-Service, Assert-OwnedService, Test-ServiceExecutableOwned,
    New-HttpsIisSite, Write-DeploymentMetadata, Read-DeploymentMetadata
