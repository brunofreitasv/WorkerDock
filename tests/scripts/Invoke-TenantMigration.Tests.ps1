$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scriptPath = Join-Path $repoRoot 'scripts/Invoke-TenantMigration.ps1'
$assemblyDirectory = Join-Path $repoRoot 'src/WorkerDock.Web/bin/Release/net10.0'

if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw 'Migration script is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $assemblyDirectory 'WorkerDock.Web.deps.json') -PathType Leaf)) {
    throw 'Build WorkerDock.Web in Release configuration before running this test.'
}

$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Migration script parse failed: $($parseErrors[0].Message)" }

$harness = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.ps1')
$source = @'
param([string]$ScriptPath, [string]$Case, [string]$ScriptsRoot, [string]$AssemblyDirectory)
$ErrorActionPreference = 'Stop'
$credential = [pscredential]::new('migration_user', (ConvertTo-SecureString 'SENTINEL-password' -AsPlainText -Force))
$common = @{
    Action = 'Apply'; SqlHost = 'sql.example.test'; Port = 1433; Database = 'tenant'
    Credential = $credential; InstanceId = [guid]::NewGuid(); RuntimePrincipal = 'WorkerDock_Runtime'
    TargetMigration = '0001_initial'
    ScriptsRoot = $ScriptsRoot; AssemblyDirectory = $AssemblyDirectory
}
switch ($Case) {
    'host' { $common.SqlHost = 'bad;host' }
    'port' { $common.Port = 'not-a-port' }
    'assembly' { $common.AssemblyDirectory = Join-Path $PSScriptRoot 'missing-assemblies' }
    'credential' { $common.Credential = [pscredential]::Empty }
    'action' { $common.Action = 'Deploy' }
    'instance' { $common.InstanceId = 'not-a-guid' }
    'missing' { $common.Remove('Port') }
    'initial-setup' { $common.Remove('InstanceId'); $common.InitialSetup = $true; $common.SqlHost = '127.0.0.1'; $common.Port = '1' }
    'existing-apply' { $common.InstanceId = [string][guid]::NewGuid(); $common.SqlHost = '127.0.0.1'; $common.Port = '1' }
    'existing-rollback' { $common.Action = 'Rollback'; $common.SqlHost = '127.0.0.1'; $common.Port = '1' }
    'absent-id' { $common.Remove('InstanceId') }
    'malformed-id' { $common.InstanceId = 'not-a-guid' }
    'both-modes' { $common.InitialSetup = $true }
    'both-modes-empty-id' { $common.InstanceId = ''; $common.InitialSetup = $true }
    'initial-rollback' { $common.Action = 'Rollback'; $common.Remove('InstanceId'); $common.InitialSetup = $true }
    'failed-initial' { $common.Remove('InstanceId'); $common.InitialSetup = $true; $common.SqlHost = '127.0.0.1'; $common.Port = '1' }
    'runtime-principal' { $common.Remove('RuntimePrincipal') }
    'valid-types' {
        $common.SqlHost = '127.0.0.1'
        $common.Port = '1'
        $common.InstanceId = [string][guid]::NewGuid()
    }
}
try { & $ScriptPath @common }
catch { [Console]::Error.WriteLine('HarnessInvocationFailed'); exit 1 }
'@
Set-Content -LiteralPath $harness -Value $source -Encoding utf8
try {
    foreach ($case in 'host', 'port', 'assembly', 'credential', 'action', 'instance', 'missing',
            'absent-id', 'malformed-id', 'both-modes', 'both-modes-empty-id', 'initial-rollback', 'runtime-principal', 'failed-initial',
            'initial-setup', 'existing-apply', 'existing-rollback', 'valid-types') {
        $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($harness)
        $start.ArgumentList.Add($scriptPath)
        $start.ArgumentList.Add($case)
        $start.ArgumentList.Add((Join-Path $repoRoot 'database'))
        $start.ArgumentList.Add($assemblyDirectory)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $output = $stdout + $stderr
        if ($process.ExitCode -eq 0) { throw "Case '$case' unexpectedly succeeded. Output: $output" }
        if ($output.Contains('SENTINEL-password')) { throw "Case '$case' exposed a password." }
        if ($case -in 'host', 'port', 'missing' -and -not $output.Contains('SqlConfigurationInvalid')) {
            throw "Case '$case' did not report SqlConfigurationInvalid."
        }
        if ($case -eq 'action' -and -not $output.Contains('ActionInvalid')) { throw 'Invalid action did not report ActionInvalid.' }
        if ($case -eq 'instance' -and -not $output.Contains('InstanceIdInvalid')) { throw 'Invalid instance ID did not report InstanceIdInvalid.' }
        if ($case -eq 'credential' -and -not $output.Contains('CredentialInvalid')) {
            throw 'Empty credentials did not report CredentialInvalid.'
        }
        if ($case -eq 'assembly' -and -not $output.Contains('AssemblyDirectoryInvalid')) {
            throw 'Missing assemblies did not report AssemblyDirectoryInvalid.'
        }
        if ($case -eq 'valid-types' -and -not $output.Contains('PersistenceUnavailable')) {
            throw "Valid string inputs did not reach the migration runner with converted types. Output: $output"
        }
        if ($case -in 'initial-setup', 'existing-apply', 'existing-rollback' -and -not $output.Contains('PersistenceUnavailable')) {
            throw "Valid mode '$case' did not reach the migration runner. Output: $output"
        }
        if ($case -in 'absent-id', 'malformed-id' -and -not $output.Contains('InstanceIdInvalid')) {
            throw "Mode '$case' did not report InstanceIdInvalid. Output: $output"
        }
        if ($case -in 'both-modes', 'both-modes-empty-id', 'initial-rollback' -and -not $output.Contains('MigrationModeInvalid')) {
            throw "Invalid mode '$case' did not report MigrationModeInvalid. Output: $output"
        }
        if ($case -eq 'failed-initial' -and $stdout -match '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}') {
            throw 'Failed initial setup printed a generated instance ID.'
        }
        if ($case -eq 'runtime-principal' -and -not $output.Contains('RuntimePrincipalInvalid')) {
            throw "Missing runtime principal did not report RuntimePrincipalInvalid. Output: $output"
        }
    }
}
finally {
    Remove-Item -LiteralPath $harness -Force -ErrorAction SilentlyContinue
}
