$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scriptPath = Join-Path $repoRoot 'scripts/Invoke-TenantMigration.ps1'

if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw 'Migration script is missing.'
}

$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Migration script parse failed: $($parseErrors[0].Message)" }

$harness = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.ps1')
$source = @'
param([string]$ScriptPath, [string]$Case, [string]$ScriptsRoot)
$credential = [pscredential]::new('migration_user', (ConvertTo-SecureString 'SENTINEL-password' -AsPlainText -Force))
$common = @{
    Action = 'Apply'; SqlHost = 'sql.example.test'; Port = 1433; Database = 'tenant'
    Credential = $credential; InstanceId = [guid]::NewGuid(); TargetMigration = '0001_bootstrap'
    ScriptsRoot = $ScriptsRoot; AssemblyDirectory = $PSScriptRoot
}
switch ($Case) {
    'host' { $common.SqlHost = 'bad;host' }
    'port' { $common.Port = 'not-a-port' }
    'assembly' { $common.AssemblyDirectory = Join-Path $PSScriptRoot 'missing-assemblies' }
    'credential' { $common.Credential = [pscredential]::Empty }
    'action' { $common.Action = 'Deploy' }
    'instance' { $common.InstanceId = 'not-a-guid' }
    'missing' { $common.Remove('Port') }
}
& $ScriptPath @common
'@
Set-Content -LiteralPath $harness -Value $source -Encoding utf8
try {
    foreach ($case in 'host', 'port', 'assembly', 'credential', 'action', 'instance', 'missing') {
        $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($harness)
        $start.ArgumentList.Add($scriptPath)
        $start.ArgumentList.Add($case)
        $start.ArgumentList.Add((Join-Path $repoRoot 'database'))
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
    }
}
finally {
    Remove-Item -LiteralPath $harness -Force -ErrorAction SilentlyContinue
}
