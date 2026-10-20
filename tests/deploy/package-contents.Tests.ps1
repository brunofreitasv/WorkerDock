[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TenantPackagePath,
    [Parameter(Mandatory)][string] $CenterPackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-PackageContents([string] $Path, [string] $Kind) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($entries | Where-Object { $_ -match '(^|/)database/(central|upstream)/' }) {
            throw "$Kind package contains retired central or upstream SQL."
        }
        if ($Kind -eq 'center') {
            if ($entries | Where-Object { $_ -match '(^|/)tools/database-migrator/' }) {
                throw 'Center package contains a database migrator.'
            }
            if ($entries | Where-Object { $_ -match '(^|/)database/' }) {
                throw 'Center package contains SQL assets.'
            }
            return
        }

        $manifestEntry = $archive.GetEntry('tools/database-migrator/database/tenant/manifest.json')
        if (-not $manifestEntry) { throw 'Tenant package is missing its tenant migration manifest.' }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $migrations = @($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
        foreach ($migration in $migrations) {
            foreach ($direction in @('up', 'down')) {
                $script = "tools/database-migrator/database/tenant/$($migration.id).$direction.sql"
                if ($entries -notcontains $script) { throw "Tenant package is missing $script." }
            }
        }
    } finally { $archive.Dispose() }
}

Assert-PackageContents $TenantPackagePath 'tenant'
Assert-PackageContents $CenterPackagePath 'center'
Write-Output 'pass'
