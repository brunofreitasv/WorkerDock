[CmdletBinding()]
param(
    [string]$Action,
    [string]$SqlHost,
    [string]$Port,
    [string]$Database,
    [object]$Credential,
    [string]$InstanceId,
    [switch]$InitialSetup,
    [string]$RuntimePrincipal,
    [string]$TargetMigration,
    [string]$ScriptsRoot,
    [string]$AssemblyDirectory
)

$ErrorActionPreference = 'Stop'
$password = $null

function Stop-WithCode([string]$Code) {
    throw [System.InvalidOperationException]::new($Code)
}

try {
    if ($Action -notin 'Apply', 'Rollback') { Stop-WithCode 'ActionInvalid' }
    $parsedPort = 0
    if (-not [int]::TryParse($Port, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedPort)) {
        Stop-WithCode 'SqlConfigurationInvalid'
    }
    $generatedInstanceId = $false
    $parsedInstanceId = [guid]::Empty
    if ($InitialSetup) {
        if ($PSBoundParameters.ContainsKey('InstanceId') -or $Action -ne 'Apply') { Stop-WithCode 'MigrationModeInvalid' }
        $parsedInstanceId = [guid]::NewGuid()
        $generatedInstanceId = $true
    }
    elseif (-not [guid]::TryParse($InstanceId, [ref]$parsedInstanceId)) {
        Stop-WithCode 'InstanceIdInvalid'
    }
    if ([string]::IsNullOrWhiteSpace($RuntimePrincipal) -or $RuntimePrincipal.Length -gt 128) {
        Stop-WithCode 'RuntimePrincipalInvalid'
    }
    if ($null -eq $Credential -or $Credential -isnot [System.Management.Automation.PSCredential] -or
        [string]::IsNullOrEmpty($Credential.UserName) -or
        $null -eq $Credential.Password -or $Credential.Password.Length -eq 0) {
        Stop-WithCode 'CredentialInvalid'
    }
    $validHost = $false
    if (-not [string]::IsNullOrEmpty($SqlHost) -and $SqlHost.Length -le 253 -and
        -not [regex]::IsMatch($SqlHost, '[\s,\\;=/\[\]]')) {
        $parsedAddress = $null
        if ([System.Net.IPAddress]::TryParse($SqlHost, [ref]$parsedAddress)) {
            $validHost = $true
        }
        else {
            $dnsName = $SqlHost.TrimEnd('.')
            $labels = $dnsName.Split('.')
            $validHost = $dnsName.Length -gt 0 -and @($labels | Where-Object {
                $_.Length -eq 0 -or $_.Length -gt 63 -or $_ -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$'
            }).Count -eq 0
        }
    }
    if (-not $validHost -or $parsedPort -lt 1 -or $parsedPort -gt 65535 -or
        [string]::IsNullOrEmpty($Database) -or $Database.Length -gt 128 -or [regex]::IsMatch($Database, '[\x00-\x1f]') -or
        $Credential.UserName.Length -gt 128 -or [regex]::IsMatch($Credential.UserName, '[\x00-\x1f]')) {
        Stop-WithCode 'SqlConfigurationInvalid'
    }
    if ($TargetMigration -notmatch '^[A-Za-z0-9_]+$') { Stop-WithCode 'UnknownMigration' }

    if (-not (Test-Path -LiteralPath $ScriptsRoot -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $ScriptsRoot 'tenant/manifest.json') -PathType Leaf)) {
        Stop-WithCode 'ScriptsRootInvalid'
    }
    if (-not (Test-Path -LiteralPath $AssemblyDirectory -PathType Container)) {
        Stop-WithCode 'AssemblyDirectoryInvalid'
    }
    $webAssembly = Join-Path $AssemblyDirectory 'WorkerDock.Web.dll'
    $webDeps = Join-Path $AssemblyDirectory 'WorkerDock.Web.deps.json'
    if (-not (Test-Path -LiteralPath $webAssembly -PathType Leaf) -or
        -not (Test-Path -LiteralPath $webDeps -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $AssemblyDirectory 'WorkerDock.Infrastructure.dll') -PathType Leaf)) {
        Stop-WithCode 'AssemblyDirectoryInvalid'
    }

    if (-not ('WorkerDockMigrationAssemblyLoadContext' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
public sealed class WorkerDockMigrationAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    public WorkerDockMigrationAssemblyLoadContext(string componentPath) : base("WorkerDock manual migration", false)
        => resolver = new AssemblyDependencyResolver(componentPath);
    protected override Assembly Load(AssemblyName name)
    {
        string path = resolver.ResolveAssemblyToPath(name);
        return path == null ? null : LoadFromAssemblyPath(path);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string path = resolver.ResolveUnmanagedDllToPath(name);
        return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
    public string Resolve(string name) => resolver.ResolveAssemblyToPath(new AssemblyName(name));
}
'@
    }

    $loadContext = [WorkerDockMigrationAssemblyLoadContext]::new($webAssembly)
    $infrastructurePath = $loadContext.Resolve('WorkerDock.Infrastructure')
    if ([string]::IsNullOrEmpty($infrastructurePath) -or
        [System.IO.Path]::GetFullPath($infrastructurePath) -ne [System.IO.Path]::GetFullPath((Join-Path $AssemblyDirectory 'WorkerDock.Infrastructure.dll'))) {
        Stop-WithCode 'AssemblyDirectoryInvalid'
    }
    $infrastructure = $loadContext.LoadFromAssemblyPath($infrastructurePath)
    $builderType = $infrastructure.GetType('WorkerDock.Infrastructure.Persistence.TenantSqlConnectionBuilder', $true)
    $password = $Credential.GetNetworkCredential().Password
    $connectionString = $builderType.GetMethod('BuildWithPassword').Invoke($null, [object[]]@(
        $SqlHost, $parsedPort, $Database, $Credential.UserName, $password))

    $runnerType = $infrastructure.GetType('WorkerDock.Infrastructure.Migrations.MigrationRunner', $true)
    $runner = [Activator]::CreateInstance($runnerType, [object[]]@($connectionString, $parsedInstanceId, $RuntimePrincipal, $ScriptsRoot))
    $methodName = if ($Action -eq 'Apply') { 'ApplyAsync' } else { 'RollbackAsync' }
    $operation = $runnerType.GetMethod($methodName).Invoke($runner, [object[]]@('tenant', $TargetMigration, [Threading.CancellationToken]::None))
    $operation.AsTask().GetAwaiter().GetResult()
    if ($generatedInstanceId) { [Console]::Out.WriteLine($parsedInstanceId.ToString()) }
}
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $code = 'MigrationFailed'
    $codeProperty = $exception.GetType().GetProperty('Code')
    if ($null -ne $codeProperty) {
        $candidate = $codeProperty.GetValue($exception)
        if ($candidate -is [string] -and $candidate -match '^[A-Za-z][A-Za-z0-9]{0,79}$') { $code = $candidate }
    }
    elseif ($exception.Message -in 'ActionInvalid', 'SqlConfigurationInvalid', 'InstanceIdInvalid', 'MigrationModeInvalid', 'RuntimePrincipalInvalid', 'ScriptsRootInvalid', 'AssemblyDirectoryInvalid', 'CredentialInvalid', 'UnknownMigration') {
        $code = $exception.Message
    }
    [Console]::Error.WriteLine($code)
    $password = $null
    $connectionString = $null
    [Environment]::Exit(1)
}
finally {
    $password = $null
    $connectionString = $null
}
