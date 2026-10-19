[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputPath = 'docs/THIRD-PARTY-NOTICES-dotnet.md'
)

$ErrorActionPreference = 'Stop'
$packageRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE '.nuget/packages'
}
$packages = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)

Get-ChildItem -LiteralPath $RepositoryRoot -Filter packages.lock.json -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' } |
    ForEach-Object {
        $lock = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            foreach ($entry in $framework.Value.PSObject.Properties) {
                if (-not $entry.Value.resolved) { continue }
                $key = '{0}/{1}' -f $entry.Name.ToLowerInvariant(), $entry.Value.resolved.ToLowerInvariant()
                if (-not $packages.ContainsKey($key)) {
                    $packages[$key] = [pscustomobject]@{ Id = $entry.Name; Version = [string]$entry.Value.resolved }
                }
            }
        }
    }

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# .NET third-party notices')
$lines.Add('')
$lines.Add('Generated from all repository `packages.lock.json` files and the corresponding NuGet package metadata. This inventory includes development and test dependencies as well as runtime dependencies. Each package retains its own license and copyright terms; consult the linked package license and the package contents for required notices.')
$lines.Add('')
$lines.Add('| Package | Version | License | Authors | Project |')
$lines.Add('| --- | --- | --- | --- | --- |')
$licenseFiles = [System.Collections.Generic.List[object]]::new()

foreach ($package in ($packages.Values | Sort-Object Id, Version)) {
    $folder = Join-Path $packageRoot (Join-Path $package.Id.ToLowerInvariant() $package.Version.ToLowerInvariant())
    $nuspec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $nuspec) { throw "NuGet package metadata is missing for $($package.Id) $($package.Version). Restore locked packages before generating notices." }
    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $xml.package.metadata
    $license = if ($metadata.license) {
        '{0}: {1}' -f $metadata.license.type, $metadata.license.InnerText
    } elseif ($metadata.licenseUrl) {
        [string]$metadata.licenseUrl
    } else {
        throw "License metadata is missing for $($package.Id) $($package.Version). Review manually before release."
    }
    if ($metadata.license.type -eq 'file') {
        $licenseFilePath = Join-Path $folder ([string]$metadata.license.InnerText)
        if (-not (Test-Path -LiteralPath $licenseFilePath -PathType Leaf)) {
            throw "Declared license file is missing for $($package.Id) $($package.Version): $($metadata.license.InnerText)"
        }
        $licenseFiles.Add([pscustomobject]@{
            Id = $package.Id
            Version = $package.Version
            Name = [string]$metadata.license.InnerText
            Content = (((Get-Content -LiteralPath $licenseFilePath -Raw).Trim() -split "`r?`n") | ForEach-Object { $_.TrimEnd() }) -join "`n"
        })
    }
    $authors = ([string]$metadata.authors) -replace '\|', '\|'
    $project = if ($metadata.projectUrl) { [string]$metadata.projectUrl } else { '' }
    $license = $license -replace '\|', '\|'
    $project = $project -replace '\|', '\|'
    $row = '| {0} | {1} | {2} | {3} | {4} |' -f @($package.Id, $package.Version, $license, $authors, $project)
    $lines.Add($row)
}

if ($licenseFiles.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Included license files')
    foreach ($licenseFile in $licenseFiles) {
        $lines.Add('')
        $lines.Add('### {0} {1} — `{2}`' -f @($licenseFile.Id, $licenseFile.Version, $licenseFile.Name))
        $lines.Add('')
        $lines.Add('```text')
        $lines.Add($licenseFile.Content)
        $lines.Add('```')
    }
}

$destination = Join-Path $RepositoryRoot $OutputPath
$directory = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $directory -Force | Out-Null
Set-Content -LiteralPath $destination -Value ($lines -join "`n") -Encoding utf8
Write-Output "Generated $($packages.Count) package entries at $destination"
