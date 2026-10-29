[CmdletBinding()]
param(
    [ValidateRange(1, 100)][int] $Tenants = 10,
    [ValidateRange(2, 1000)][int] $JobsPerTenant = 100,
    [ValidateRange(1, 100)][int] $ConcurrencyPerTenant = 10,
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateRange(1, 3600)][int] $ExecutionTimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-Workload([string] $Message) { throw "Workload not started: $Message" }
function Test-LoopbackUri([uri] $Uri) {
    if ($Uri.IsLoopback) { return $true }
    if ($Uri.HostNameType -eq [UriHostNameType]::IPv4) { return [Net.IPAddress]::Parse($Uri.Host).IsLoopback }
    if ($Uri.HostNameType -eq [UriHostNameType]::IPv6) { return [Net.IPAddress]::Parse($Uri.Host).IsLoopback }
    return $false
}
function Get-TargetAddresses([uri] $Uri) {
    try { return @([Net.Dns]::GetHostAddresses($Uri.DnsSafeHost)) }
    catch { Stop-Workload "unable to resolve fixture host '$($Uri.DnsSafeHost)'." }
}
function Test-UnsafeFixtureAddress([Net.IPAddress] $Address) {
    if ($Address.IsIPv4MappedToIPv6) { $Address = $Address.MapToIPv4() }
    if ([Net.IPAddress]::IsLoopback($Address)) { return $false }
    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
        $b = $Address.GetAddressBytes()
        if (($b[0] -eq 169 -and $b[1] -eq 254) -or ($b[0] -eq 168 -and $b[1] -eq 63 -and $b[2] -eq 129 -and $b[3] -eq 16)) { return $true }
        return ($b[0] -eq 0 -or $b[0] -eq 10 -or $b[0] -eq 127 -or $b[0] -ge 224 -or
            ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or
            ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or
            ($b[0] -eq 192 -and ($b[1] -eq 168 -or $b[1] -eq 0 -or $b[1] -eq 2)) -or
            ($b[0] -eq 198 -and ($b[1] -eq 18 -or $b[1] -eq 19 -or $b[1] -eq 51)) -or
            ($b[0] -eq 203 -and $b[1] -eq 0 -and $b[2] -eq 113))
    }
    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
        return ($Address.IsIPv6LinkLocal -or $Address.IsIPv6SiteLocal -or $Address.IsIPv6Multicast -or
            $Address.Equals([Net.IPAddress]::IPv6Any) -or $Address.IsIPv6UniqueLocal)
    }
    return $true
}
function Get-SessionAndToken([string] $BaseUrl, [bool] $SkipCertificateCheck) {
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $common = @{ WebSession = $session; TimeoutSec = 15; ErrorAction = 'Stop' }
    if ($SkipCertificateCheck) { $common['SkipCertificateCheck'] = $true }
    $identity = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/session" @common
    $csrf = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/antiforgery" @common
    if ($identity.mode -ne 'Anonymous' -or $identity.authenticated -or -not $identity.canManage -or [string]::IsNullOrWhiteSpace([string]$csrf.requestToken)) {
        throw 'Target does not provide the required anonymous management session.'
    }
    return @{ Session = $session; Token = [string]$csrf.requestToken }
}
function Get-Percentile([double[]] $Values, [double] $P) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling($P * $sorted.Count) - 1
    return [Math]::Round($sorted[[Math]::Max(0, $index)], 2)
}
function Get-HostCpuPercent {
    try { return [Math]::Round([double](Get-CimInstance -ClassName Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average, 2) }
    catch { return $null }
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { Stop-Workload 'ConfigPath does not exist.' }
$resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
try { $config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json -AsHashtable }
catch { Stop-Workload 'ConfigPath must contain valid JSON.' }
foreach ($key in 'schemaVersion','Environment','AllowAnonymousManagement','AllowRemoteHosts','AllowedFixtureHosts','AllowPrivateFixtureHost','FixtureEgressEnforced','SqlServerVersion','SqlServerResources','Tenants') {
    if (-not $config.ContainsKey($key)) { Stop-Workload "configuration is missing required property '$key'." }
}
if ($config.schemaVersion -ne 1) { Stop-Workload 'configuration schemaVersion must be 1.' }
if ($config.Environment -cne 'disposable') { Stop-Workload 'configuration Environment must equal "disposable".' }
if ($config.AllowAnonymousManagement -cne $true) { Stop-Workload 'configuration must explicitly set AllowAnonymousManagement to true; authenticated/OIDC targets are unsupported.' }
if ($config.AllowRemoteHosts -isnot [bool]) { Stop-Workload 'configuration must explicitly set AllowRemoteHosts to true or false.' }
if ($config.AllowPrivateFixtureHost -isnot [bool]) { Stop-Workload 'configuration must explicitly set AllowPrivateFixtureHost to true or false.' }
if ($config.FixtureEgressEnforced -cne $true) { Stop-Workload 'configuration must explicitly confirm FixtureEgressEnforced=true; worker-side host and port egress restrictions must be configured independently.' }
if ($config.AllowedFixtureHosts -isnot [array]) { Stop-Workload 'AllowedFixtureHosts must be an explicit array of exact host names; wildcards are unsupported.' }
foreach ($allowedHost in $config.AllowedFixtureHosts) { if ([string]$allowedHost -notmatch '^(?=.{1,253}$)[A-Za-z0-9.-]+$' -or [string]$allowedHost -match '\.\.|^\.|\.$') { Stop-Workload 'AllowedFixtureHosts contains an invalid exact host name.' } }
if ([string]$config.SqlServerVersion -notmatch '^[A-Za-z0-9 .,+()/_-]{1,100}$') { Stop-Workload 'SqlServerVersion must be a short non-secret version label.' }
if ($config.SqlServerResources -isnot [System.Collections.IDictionary] -or -not $config.SqlServerResources.ContainsKey('vCpu') -or -not $config.SqlServerResources.ContainsKey('memoryGiB') -or $config.SqlServerResources.vCpu -isnot [ValueType] -or $config.SqlServerResources.vCpu -is [bool] -or $config.SqlServerResources.memoryGiB -isnot [ValueType] -or $config.SqlServerResources.memoryGiB -is [bool]) { Stop-Workload 'SqlServerResources must contain numeric, non-boolean vCpu and memoryGiB values.' }
if ([double]$config.SqlServerResources.vCpu -le 0 -or [double]$config.SqlServerResources.memoryGiB -le 0) { Stop-Workload 'SQL Server resource values must be positive.' }
if ($config.Tenants -isnot [array] -or $config.Tenants.Count -lt $Tenants) { Stop-Workload "configuration must provide at least $Tenants tenant entries." }
if (($Tenants * $ConcurrencyPerTenant) -gt 100) { Stop-Workload 'total concurrent requests across selected tenants may not exceed 100.' }
if (-not (Test-Path -LiteralPath $OutputPath -PathType Container)) { Stop-Workload 'OutputPath must be an existing directory.' }
$resolvedOutput = (Resolve-Path -LiteralPath $OutputPath).Path
if ($resolvedConfig.StartsWith($resolvedOutput.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Stop-Workload 'ConfigPath must be kept outside OutputPath.' }

$selected = @($config.Tenants | Select-Object -First $Tenants)
$seenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seenInstances = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($tenant in $selected) {
    if ($tenant -isnot [System.Collections.IDictionary]) { Stop-Workload 'each tenant entry must be a JSON object.' }
    foreach ($key in 'Name','InstanceId','BaseUrl','FixtureUrl') { if (-not $tenant.ContainsKey($key)) { Stop-Workload "each tenant entry requires '$key'." } }
    if ([string]::IsNullOrWhiteSpace([string]$tenant.Name) -or [string]::IsNullOrWhiteSpace([string]$tenant.BaseUrl) -or [string]::IsNullOrWhiteSpace([string]$tenant.FixtureUrl)) { Stop-Workload 'each selected tenant requires Name, BaseUrl, and FixtureUrl.' }
    if ([string]$tenant.Name -notmatch '^[A-Za-z0-9_-]{1,64}$') { Stop-Workload 'tenant Name must be a short non-secret identifier.' }
    $parsedInstanceId = [guid]::Empty
    if (-not [guid]::TryParse([string]$tenant.InstanceId, [ref]$parsedInstanceId)) { Stop-Workload 'each selected tenant requires a valid InstanceId.' }
    if (-not $seenNames.Add([string]$tenant.Name) -or -not $seenInstances.Add($parsedInstanceId.ToString('D'))) { Stop-Workload 'selected tenant names and instance IDs must be unique.' }
    foreach ($field in 'BaseUrl', 'FixtureUrl') {
        $uri = $null
        if (-not [uri]::TryCreate([string]$tenant.$field, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') { Stop-Workload "tenant $field must be an absolute HTTPS URL." }
        if (-not $config.AllowRemoteHosts -and -not (Test-LoopbackUri $uri)) { Stop-Workload "tenant $field must use a loopback host unless AllowRemoteHosts is true." }
        if ($uri.UserInfo -or $uri.Query -or $uri.Fragment) { Stop-Workload "tenant $field must not contain credentials, a query, or a fragment." }
    }
    $baseUri = [uri]$tenant.BaseUrl
    $fixtureUri = [uri]$tenant.FixtureUrl
    if (-not (Test-LoopbackUri $fixtureUri)) {
        if (-not $config.AllowRemoteHosts) { Stop-Workload 'remote FixtureUrl requires AllowRemoteHosts=true.' }
        if (-not (@($config.AllowedFixtureHosts | ForEach-Object { ([string]$_).ToLowerInvariant() }) -contains $fixtureUri.DnsSafeHost.ToLowerInvariant())) { Stop-Workload 'remote FixtureUrl host must exactly match an entry in AllowedFixtureHosts.' }
        $sameHost = [string]::Equals($fixtureUri.DnsSafeHost, $baseUri.DnsSafeHost, [StringComparison]::OrdinalIgnoreCase)
        foreach ($address in (Get-TargetAddresses $fixtureUri)) {
            if ([string]$address -eq 'fd00:ec2::254') { Stop-Workload 'cloud metadata fixture destinations are not supported.' }
            if (Test-UnsafeFixtureAddress $address) {
                if (-not ($sameHost -and $config.AllowPrivateFixtureHost)) { Stop-Workload 'FixtureUrl resolves to a private, link-local, reserved, or metadata address; private fixtures require an explicit same-host isolated-fixture allowance.' }
                $b = $address.GetAddressBytes()
                if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and (($b[0] -eq 169 -and $b[1] -eq 254) -or ($b[0] -eq 168 -and $b[1] -eq 63 -and $b[2] -eq 129 -and $b[3] -eq 16))) { Stop-Workload 'link-local and metadata fixture destinations are not supported.' }
            }
        }
    }
}

# Preflight all tenants before writes: target identity, HTTP fixture and SQL-backed
# execution projections must all be available. Never skip TLS checks for remote hosts.
foreach ($tenant in $selected) {
    $base = ([uri]$tenant.BaseUrl).AbsoluteUri.TrimEnd('/')
    $skipTls = Test-LoopbackUri ([uri]$tenant.BaseUrl)
    try {
        $access = Get-SessionAndToken $base $skipTls
        $healthParams = @{ Method = 'Get'; Uri = "$base/health/live"; TimeoutSec = 15; ErrorAction = 'Stop' }
        if ($skipTls) { $healthParams['SkipCertificateCheck'] = $true }
        $null = Invoke-RestMethod @healthParams
        $dashboardParams = @{ Method = 'Get'; Uri = "$base/api/monitoring"; WebSession = $access.Session; TimeoutSec = 15; ErrorAction = 'Stop' }
        if ($skipTls) { $dashboardParams['SkipCertificateCheck'] = $true }
        $dashboard = Invoke-RestMethod @dashboardParams
        if ([string]$dashboard.instanceId -ne [string]$tenant.InstanceId) { Stop-Workload "configured InstanceId does not match tenant '$($tenant.Name)' monitoring identity." }
        $detailParams = @{ Method = 'Get'; Uri = "$base/api/monitoring/executions/$([guid]::NewGuid())"; WebSession = $access.Session; TimeoutSec = 15; ErrorAction = 'Stop' }
        if ($skipTls) { $detailParams['SkipCertificateCheck'] = $true }
        $probe = $null
        try { $probe = Invoke-RestMethod @detailParams } catch {
            if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
        }
        if ($null -ne $probe) { Stop-Workload "unexpected execution detail response for a random ID at tenant '$($tenant.Name)'." }
        $workersParams = @{ Method = 'Get'; Uri = "$base/api/monitoring/workers"; WebSession = $access.Session; TimeoutSec = 15; ErrorAction = 'Stop' }
        if ($skipTls) { $workersParams['SkipCertificateCheck'] = $true }
        $workers = @(Invoke-RestMethod @workersParams)
        $tenant.WorkerCount = $workers.Count
        $tenant.HttpWorkerCount = @($workers | Where-Object { $_.State -eq 'Ready' -and @($_.Capabilities) -contains 'workerdock.http@1.1.0' }).Count
        if ($tenant.HttpWorkerCount -lt 1) { Stop-Workload "tenant '$($tenant.Name)' has no ready worker advertising workerdock.http@1.1.0." }
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $client = [Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        try {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, [string]$tenant.FixtureUrl)
            try {
                $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                try { if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw 'Fixture did not return HTTP 200.' } }
                finally { $response.Dispose() }
            }
            finally { $request.Dispose() }
        }
        finally { $client.Dispose(); $handler.Dispose() }
    }
    catch { Stop-Workload "preflight failed for tenant '$($tenant.Name)' (session, SQL execution detail, HTTPS health, or fixture); no jobs were created." }
}

$runId = [guid]::NewGuid().ToString('N')
$invariantViolations = [System.Collections.Generic.List[string]]::new()
$allStart = [DateTimeOffset]::UtcNow
$hostCpuStart = Get-HostCpuPercent
$fixtureResults = @($selected | ForEach-Object -Parallel {
    $tenant = $_
    $base = ([uri]$tenant.BaseUrl).AbsoluteUri.TrimEnd('/')
    $skipTls = ([uri]$tenant.BaseUrl).IsLoopback
    $origin = $base
    $runId = $using:runId
    $jobCount = $using:JobsPerTenant
    $concurrency = $using:ConcurrencyPerTenant
    $timeoutSeconds = $using:ExecutionTimeoutSeconds
    $created = [System.Collections.Generic.List[object]]::new()
    $observations = [System.Collections.Generic.List[object]]::new()
    $unresolvedExecutionIds = [System.Collections.Generic.List[string]]::new()
    $leaveUnarchivedJobIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $failure = $null
    $warmObservation = $null
    $measurementStart = $null
    $measurementEnd = $null

    function New-Access {
        $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $options = @{ WebSession = $session; TimeoutSec = 20; ErrorAction = 'Stop' }
        if ($skipTls) { $options['SkipCertificateCheck'] = $true }
        $null = Invoke-RestMethod -Method Get -Uri "$base/api/session" @options
        $csrf = Invoke-RestMethod -Method Get -Uri "$base/api/antiforgery" @options
        return @{ Session = $session; Token = [string]$csrf.requestToken }
    }
    function Call-Api($Access, [string]$Method, [string]$Path, $Body = $null, [string]$ETag = $null) {
        $headers = @{ Origin = $origin; 'X-WorkerDock-CSRF' = $Access.Token }
        if ($ETag) { $headers['If-Match'] = $ETag }
        $options = @{ Method = $Method; Uri = "$base$Path"; WebSession = $Access.Session; Headers = $headers; TimeoutSec = 30; ErrorAction = 'Stop' }
        if ($skipTls) { $options['SkipCertificateCheck'] = $true }
        if ($null -ne $Body) { $options['ContentType'] = 'application/json'; $options['Body'] = ($Body | ConvertTo-Json -Depth 12 -Compress) }
        Invoke-RestMethod @options
    }
    function Get-Json([string]$Uri, $Session) {
        $options = @{ WebSession=$Session; TimeoutSec=20; ErrorAction='Stop' }
        if ($skipTls) { $options['SkipCertificateCheck']=$true }
        Invoke-RestMethod -Method Get -Uri $Uri @options
    }
    function Get-Response([string]$Uri, $Session) {
        $options = @{ WebSession=$Session; TimeoutSec=20; ErrorAction='Stop' }
        if ($skipTls) { $options['SkipCertificateCheck']=$true }
        Invoke-WebRequest -Method Get -Uri $Uri @options
    }

    try {
        # Warm one execution per tenant and exclude it from measured throughput.
        $count = $jobCount
        $jobsToCreate = $count
        for ($index = 0; $index -lt $jobsToCreate; $index++) {
            $access = New-Access
            $name = "WDLoad-$runId-$index"
            $configuration = [ordered]@{ method='GET'; url=[string]$tenant.FixtureUrl; query=@{}; headers=@{}; secretHeaders=@{}; parameters=@{}; contentType=$null; body=$null; timeoutSeconds=15; successStatuses=@(200); idempotencyHeader=$null; maxResponseBytes=65536; responseMode='SafeSummary' }
            $policy = [ordered]@{ timeout='00:01:00'; maxAttempts=1; retryBaseDelay='00:00:00'; retryMaxDelay='00:00:00'; jitterRatio=0; retryableCodes=@(); retryUncertain=$false; overlap='Allow'; backlogLimit=1; priority=5 }
            $revision = [ordered]@{ jobId='00000000-0000-0000-0000-000000000000'; revision=0; name=$name; templateId='workerdock.http'; templateVersion='1.1.0'; configuration=$configuration; profileId=$null; policy=$policy }
            $jobId = $null
            try {
                $saved = Call-Api $access 'Post' '/api/jobs' $revision
                $parsedJobId = [guid]::Empty
                if ([guid]::TryParse([string]$saved.targetId, [ref]$parsedJobId) -and $parsedJobId -ne [guid]::Empty) { $jobId = $parsedJobId.ToString('D') }
                else { throw 'Job creation returned no valid target ID.' }
            }
            catch {
                # A write can commit even when its response is lost. Reconcile by
                # this invocation's cryptographically unique job name before failing.
                try {
                    $search = [uri]::EscapeDataString($name)
                    $reconciledJobs = @(Get-Json "$base/api/monitoring/jobs?search=$search&limit=100" $access.Session).items | Where-Object { $_.Name -ceq $name }
                    if ($reconciledJobs.Count -eq 1) {
                        $parsedJobId = [guid]::Empty
                        if ([guid]::TryParse([string]$reconciledJobs[0].JobId, [ref]$parsedJobId) -and $parsedJobId -ne [guid]::Empty) { $jobId = $parsedJobId.ToString('D') }
                    }
                } catch { }
                if (-not $jobId) {
                    $failure = 'job-create-acknowledgement-uncertain; run-scoped reconciliation found no unique job, cleanup cannot be confirmed'
                    throw 'Job creation acknowledgement was uncertain and reconciliation did not identify exactly one created job.'
                }
            }
            $created.Add([pscustomobject]@{ JobId=$jobId; Name=$name })
            $etag = (Get-Response "$base/api/jobs/$jobId/configuration" $access.Session).Headers.ETag
            $null = Call-Api $access 'Post' "/api/jobs/$jobId/enable-manual" $null $etag
        }

        $measuredIds = @($created | Select-Object -Skip 1)
        if ($measuredIds.Count -eq 0) { throw 'JobsPerTenant must leave at least one measured run after warmup.' }
        $warm = $created[0]
        $start = [DateTimeOffset]::UtcNow
        $queue = [System.Collections.Generic.Queue[object]]::new()
        $queue.Enqueue($warm)
        $warmObservation = $null
        while ($queue.Count -gt 0) {
            $batch = @($queue.Dequeue())
            foreach ($item in $batch) {
                $access = New-Access
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $receipt = Call-Api $access 'Post' "/api/jobs/$($item.JobId)/runs" @{ requestKey = "$runId-warmup" }
                $sw.Stop()
                $executionId = [string]$receipt.targetId
                $unresolvedExecutionIds.Add($executionId)
                $null = $leaveUnarchivedJobIds.Add($item.JobId)
                $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
                do {
                    Start-Sleep -Milliseconds 250
                    $detail = Get-Json "$base/api/monitoring/executions/$executionId" $access.Session
                } while ($detail.state -in @('Queued','Running','RetryWaiting') -and [DateTimeOffset]::UtcNow -lt $deadline)
                if ($detail.state -in @('Queued','Running','RetryWaiting')) {
                    try {
                        $null = Call-Api $access 'Post' "/api/executions/$executionId/stop" @{ force=$false; confirmForce=$false; reason='workload-timeout' }
                        $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
                        do { Start-Sleep -Milliseconds 250; $detail = Get-Json "$base/api/monitoring/executions/$executionId" $access.Session } while ($detail.state -in @('Queued','Running','RetryWaiting') -and [DateTimeOffset]::UtcNow -lt $stopDeadline)
                    } catch {
                        if (-not $unresolvedExecutionIds.Contains($executionId)) { $unresolvedExecutionIds.Add($executionId) }
                        $null = $leaveUnarchivedJobIds.Add($item.JobId)
                        throw 'Timed-out execution stop or status could not be confirmed.'
                    }
                    if ($detail.state -notin @('Succeeded','Failed','Stopped','TimedOut','Skipped','NeedsReview')) { if (-not $unresolvedExecutionIds.Contains($executionId)) { $unresolvedExecutionIds.Add($executionId) }; $null = $leaveUnarchivedJobIds.Add($item.JobId) }
                    else { $null = $unresolvedExecutionIds.Remove($executionId); $null = $leaveUnarchivedJobIds.Remove($item.JobId) }
                    throw "warmup execution $executionId exceeded its timeout and stop was requested."
                }
                if ($detail.state -in @('Succeeded','Failed','Stopped','TimedOut','Skipped','NeedsReview')) {
                    $null = $unresolvedExecutionIds.Remove($executionId)
                    $null = $leaveUnarchivedJobIds.Remove($item.JobId)
                }
                $warmObservation = [pscustomobject]@{ State=$detail.state; SubmissionMs=$sw.Elapsed.TotalMilliseconds; Attempts=@($detail.attempts).Count; TriggerMs=$null; QueueMs=$null; ExecutionMs=$null; EndToEndMs=$null }
                $warmStartAt = @($detail.attempts | Where-Object { $_.startedAtUtc } | Select-Object -First 1)
                $warmEndAt = @($detail.attempts | Where-Object { $_.endedAtUtc } | Select-Object -Last 1)
                $createdUtc = [DateTimeOffset]::Parse([string]$detail.createdAtUtc).ToUniversalTime()
                $availableUtc = [DateTimeOffset]::Parse([string]$detail.availableAtUtc).ToUniversalTime()
                $warmObservation.TriggerMs = ($availableUtc - $createdUtc).TotalMilliseconds
                if ($warmStartAt.Count -gt 0) { $startedUtc = [DateTimeOffset]::Parse([string]$warmStartAt[0].startedAtUtc).ToUniversalTime(); $warmObservation.QueueMs = ($startedUtc - $availableUtc).TotalMilliseconds }
                if ($warmStartAt.Count -gt 0 -and $warmEndAt.Count -gt 0) { $endedUtc = [DateTimeOffset]::Parse([string]$warmEndAt[0].endedAtUtc).ToUniversalTime(); $warmObservation.ExecutionMs = ($endedUtc - $startedUtc).TotalMilliseconds; $warmObservation.EndToEndMs = ($endedUtc - $createdUtc).TotalMilliseconds }
            }
        }
        if ($warmObservation.State -ne 'Succeeded') { throw "warmup execution ended in state $($warmObservation.State)." }

        $measurementStart = [DateTimeOffset]::UtcNow
        $pending = [System.Collections.Generic.Queue[object]]::new()
        foreach ($item in $measuredIds) { $pending.Enqueue($item) }
        while ($pending.Count -gt 0) {
            $batch = [System.Collections.Generic.List[object]]::new()
            while ($batch.Count -lt $concurrency -and $pending.Count -gt 0) { $batch.Add($pending.Dequeue()) }
            $batchResult = @($batch | ForEach-Object -Parallel {
                $item = $_
                $tenant = $using:tenant
                $base = $using:base
                $skipTls = $using:skipTls
                $origin = $using:origin
                $runId = $using:runId
                $timeoutSeconds = $using:timeoutSeconds
                $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
                $common = @{ WebSession=$session; TimeoutSec=20; ErrorAction='Stop' }
                if ($skipTls) { $common['SkipCertificateCheck']=$true }
                $result = [ordered]@{ JobId=$item.JobId; ExecutionId=$null; State='Error'; SubmissionMs=$null; TriggerMs=$null; QueueMs=$null; ExecutionMs=$null; EndToEndMs=$null; Attempts=0; SafeCodes=@(); Error=$null; Unresolved=$false }
                try {
                    $null = Invoke-RestMethod -Method Get -Uri "$base/api/session" @common
                    $csrf = Invoke-RestMethod -Method Get -Uri "$base/api/antiforgery" @common
                    $headers = @{ Origin=$origin; 'X-WorkerDock-CSRF'=[string]$csrf.requestToken }
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    $postOptions = @{ Method='Post'; Uri="$base/api/jobs/$($item.JobId)/runs"; WebSession=$session; Headers=$headers; ContentType='application/json'; Body=(@{ requestKey="$runId-$($item.JobId)" } | ConvertTo-Json -Compress); TimeoutSec=30; ErrorAction='Stop' }
                    if ($skipTls) { $postOptions['SkipCertificateCheck']=$true }
                    $receipt = Invoke-RestMethod @postOptions
                    $sw.Stop()
                    $result.SubmissionMs = $sw.Elapsed.TotalMilliseconds
                    $result.ExecutionId = [string]$receipt.targetId
                    $result.Unresolved=$true
                    $result.State = 'Queued'
                    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
                    do {
                        Start-Sleep -Milliseconds 250
                        $getOptions = @{ Method='Get'; Uri="$base/api/monitoring/executions/$($result.ExecutionId)"; WebSession=$session; TimeoutSec=20; ErrorAction='Stop' }
                        if ($skipTls) { $getOptions['SkipCertificateCheck']=$true }
                        $detail = Invoke-RestMethod @getOptions
                    } while ($detail.state -in @('Queued','Running','RetryWaiting') -and [DateTimeOffset]::UtcNow -lt $deadline)
                    if ($detail.state -in @('Queued','Running','RetryWaiting')) {
                        $result.Unresolved=$true
                        $stopHeaders = @{ Origin=$origin; 'X-WorkerDock-CSRF'=[string]$csrf.requestToken }
                        $stopOptions = @{ Method='Post'; Uri="$base/api/executions/$($result.ExecutionId)/stop"; WebSession=$session; Headers=$stopHeaders; ContentType='application/json'; Body=(@{ force=$false; confirmForce=$false; reason='workload-timeout' } | ConvertTo-Json -Compress); TimeoutSec=20; ErrorAction='Stop' }
                        if ($skipTls) { $stopOptions['SkipCertificateCheck']=$true }
                        $null = Invoke-RestMethod @stopOptions
                        $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
                        do {
                            Start-Sleep -Milliseconds 250
                            $getOptions = @{ Method='Get'; Uri="$base/api/monitoring/executions/$($result.ExecutionId)"; WebSession=$session; TimeoutSec=20; ErrorAction='Stop' }
                            if ($skipTls) { $getOptions['SkipCertificateCheck']=$true }
                            $detail = Invoke-RestMethod @getOptions
                        } while ($detail.state -in @('Queued','Running','RetryWaiting') -and [DateTimeOffset]::UtcNow -lt $stopDeadline)
                        $result.Error='execution-timeout'
                        if ($detail.state -in @('Succeeded','Failed','Stopped','TimedOut','Skipped','NeedsReview')) { $result.Unresolved=$false }
                    }
                    $result.State = [string]$detail.state
                    if ($result.State -in @('Succeeded','Failed','Stopped','TimedOut','Skipped','NeedsReview')) { $result.Unresolved=$false }
                    $result.Attempts = @($detail.attempts).Count
                    $result.SafeCodes = @($detail.attempts | ForEach-Object { $_.safeCode } | Where-Object { $_ })
                    $createdAt = [DateTimeOffset]::Parse([string]$detail.createdAtUtc).ToUniversalTime()
                    $availableAt = [DateTimeOffset]::Parse([string]$detail.availableAtUtc).ToUniversalTime()
                    $startAt = @($detail.attempts | Where-Object { $_.startedAtUtc } | Select-Object -First 1)
                    $endAt = @($detail.attempts | Where-Object { $_.endedAtUtc } | Select-Object -Last 1)
                    $result.TriggerMs = ($availableAt - $createdAt).TotalMilliseconds
                    if ($startAt.Count -gt 0) { $startedAt = [DateTimeOffset]::Parse([string]$startAt[0].startedAtUtc).ToUniversalTime(); $result.QueueMs = ($startedAt - $availableAt).TotalMilliseconds }
                    if ($endAt.Count -gt 0 -and $startAt.Count -gt 0) { $endedAt = [DateTimeOffset]::Parse([string]$endAt[0].endedAtUtc).ToUniversalTime(); $result.ExecutionMs = ($endedAt - $startedAt).TotalMilliseconds; $result.EndToEndMs = ($endedAt - $createdAt).TotalMilliseconds }
                    if ($result.State -in @('Queued','Running','RetryWaiting')) { $result.Error='execution-timeout' }
                } catch { $result.Error='request-or-monitoring-error' }
                [pscustomobject]$result
            } -ThrottleLimit $concurrency)
            foreach ($itemResult in $batchResult) {
                $observations.Add($itemResult)
                if ($itemResult.Unresolved) { $unresolvedExecutionIds.Add([string]$itemResult.ExecutionId); $null = $leaveUnarchivedJobIds.Add([string]$itemResult.JobId) }
            }
        }
        $measurementEnd = [DateTimeOffset]::UtcNow
    }
    catch { $failure = 'workload-setup-or-cleanup-failed' }
    finally {
        # Only alter jobs whose IDs came from successful create responses.
        foreach ($item in $created) {
            try {
                $access = New-Access
                $editorResponse = Get-Response "$base/api/jobs/$($item.JobId)/configuration" $access.Session
                $etag = [string]$editorResponse.Headers.ETag
                $null = Call-Api $access 'Post' "/api/jobs/$($item.JobId)/actions" @{ action='Disable' } $etag
                if ($leaveUnarchivedJobIds.Contains([string]$item.JobId)) {
                    if (-not $failure) { $failure = 'stop-unconfirmed; affected job was disabled but left unarchived for operator review' }
                    continue
                }
                $editorResponse = Get-Response "$base/api/jobs/$($item.JobId)/configuration" $access.Session
                $etag = [string]$editorResponse.Headers.ETag
                $null = Call-Api $access 'Post' "/api/jobs/$($item.JobId)/actions" @{ action='Archive' } $etag
            } catch { if (-not $failure) { $failure = 'owned-job-cleanup-failed' } }
        }
    }
    [pscustomobject]@{ Tenant=$tenant.Name; InstanceId=$tenant.InstanceId; Workers=$tenant.WorkerCount; ReadyHttpWorkers=$tenant.HttpWorkerCount; JobsCreated=$created.Count; CreatedJobIds=@($created | ForEach-Object JobId); UnresolvedExecutionIds=@($unresolvedExecutionIds); Warmup=$warmObservation; Runs=@($observations); Failure=$failure; MeasurementStartUtc=$measurementStart; MeasurementEndUtc=$measurementEnd }
} -ThrottleLimit $Tenants)

$flatRuns = @($fixtureResults | ForEach-Object { $_.Runs })
$failures = @($fixtureResults | Where-Object { $_.Failure } | ForEach-Object { $_.Failure })
$errors = @($flatRuns | Where-Object { $_.Error -or $_.State -ne 'Succeeded' })
$missingTiming = @($flatRuns | Where-Object { $null -eq $_.TriggerMs -or $null -eq $_.QueueMs -or $null -eq $_.ExecutionMs -or $null -eq $_.EndToEndMs })
$triggerValues = @($flatRuns | Where-Object { $null -ne $_.TriggerMs } | ForEach-Object { [double]$_.TriggerMs })
$latencyValues = @($flatRuns | Where-Object { $null -ne $_.ExecutionMs } | ForEach-Object { [double]$_.ExecutionMs })
$submissionValues = @($flatRuns | Where-Object { $null -ne $_.SubmissionMs } | ForEach-Object { [double]$_.SubmissionMs })
$queueValues = @($flatRuns | Where-Object { $null -ne $_.QueueMs } | ForEach-Object { [double]$_.QueueMs })
$endToEndValues = @($flatRuns | Where-Object { $null -ne $_.EndToEndMs } | ForEach-Object { [double]$_.EndToEndMs })
$measurementStarts = @($fixtureResults | Where-Object { $_.MeasurementStartUtc } | ForEach-Object { [DateTimeOffset]$_.MeasurementStartUtc })
$measurementEnds = @($fixtureResults | Where-Object { $_.MeasurementEndUtc } | ForEach-Object { [DateTimeOffset]$_.MeasurementEndUtc })
$measurementSeconds = if ($measurementStarts.Count -gt 0 -and $measurementEnds.Count -gt 0) { (($measurementEnds | Measure-Object -Maximum).Maximum - ($measurementStarts | Measure-Object -Minimum).Minimum).TotalSeconds } else { 0 }
$completed = @($flatRuns | Where-Object { $_.State -eq 'Succeeded' }).Count
$throughput = if ($measurementSeconds -gt 0) { [Math]::Round($completed / $measurementSeconds, 3) } else { 0 }
if ($failures.Count) { $invariantViolations.Add('One or more tenant workers failed or cleanup did not complete.') }
if ($errors.Count) { $invariantViolations.Add('One or more measured executions failed, timed out, or could not be observed.') }
if ($missingTiming.Count) { $invariantViolations.Add('A terminal execution is missing queue or execution timestamps.') }
if ($flatRuns.Count -ne ($Tenants * ($JobsPerTenant - 1))) { $invariantViolations.Add('Measured run count did not match the configured job count minus one warmup per tenant.') }
if ($completed -ne $flatRuns.Count) { $invariantViolations.Add('Not all measured executions reached Succeeded.') }
if (@($flatRuns | Where-Object { $_.Attempts -gt 1 }).Count -gt 0) { $invariantViolations.Add('Execution attempts exceeded the configured one-attempt job policy.') }
if (@($flatRuns | Where-Object { ($null -ne $_.TriggerMs -and $_.TriggerMs -lt 0) -or ($null -ne $_.QueueMs -and $_.QueueMs -lt 0) -or ($null -ne $_.ExecutionMs -and $_.ExecutionMs -lt 0) -or ($null -ne $_.EndToEndMs -and $_.EndToEndMs -lt 0) }).Count -gt 0) { $invariantViolations.Add('Negative execution timing indicates a clock or timestamp invariant violation.') }
$hostInfo = [ordered]@{ MachineName=[Environment]::MachineName; ProcessorCount=[Environment]::ProcessorCount; OS=[Environment]::OSVersion.VersionString }
$memory = $null
try { $memory = Get-CimInstance Win32_OperatingSystem | Select-Object @{n='TotalBytes';e={[long]$_.TotalVisibleMemorySize * 1KB}}, @{n='FreeBytes';e={[long]$_.FreePhysicalMemory * 1KB}} } catch { }
$hostCpuEnd = Get-HostCpuPercent
$result = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = if ($invariantViolations.Count) { 'FAILED' } else { 'COMPLETED' }
    startedUtc = $allStart
    completedUtc = [DateTimeOffset]::UtcNow
    host = $hostInfo
    hostCpuLoadPercentStart = $hostCpuStart
    hostCpuLoadPercentEnd = $hostCpuEnd
    hostMemory = $memory
    sqlServerVersion = [string]$config.SqlServerVersion
    sqlServerResources = [ordered]@{ vCpu=[double]$config.SqlServerResources.vCpu; memoryGiB=[double]$config.SqlServerResources.memoryGiB }
    tenantCount = $Tenants
    jobsPerTenant = $JobsPerTenant
    warmupRuns = $Tenants
    measuredRuns = $flatRuns.Count
    concurrencyPerTenant = $ConcurrencyPerTenant
    jobMix = 'Uniform workerdock.http 1.1.0 GET to configured FixtureUrl; one run per uniquely created job.'
    fixtureDurationMix = 'Uniform per tenant; no mixed-duration profile is configured.'
    measurementSeconds = [Math]::Round($measurementSeconds, 3)
    throughputSucceededPerSecond = $throughput
    triggerLatencyMs = [ordered]@{ p50=(Get-Percentile $triggerValues 0.50); p95=(Get-Percentile $triggerValues 0.95); p99=(Get-Percentile $triggerValues 0.99) }
    submissionLatencyMs = [ordered]@{ p50=(Get-Percentile $submissionValues 0.50); p95=(Get-Percentile $submissionValues 0.95); p99=(Get-Percentile $submissionValues 0.99) }
    queueLatencyMs = [ordered]@{ p50=(Get-Percentile $queueValues 0.50); p95=(Get-Percentile $queueValues 0.95); p99=(Get-Percentile $queueValues 0.99) }
    executionLatencyMs = [ordered]@{ p50=(Get-Percentile $latencyValues 0.50); p95=(Get-Percentile $latencyValues 0.95); p99=(Get-Percentile $latencyValues 0.99) }
    endToEndLatencyMs = [ordered]@{ p50=(Get-Percentile $endToEndValues 0.50); p95=(Get-Percentile $endToEndValues 0.95); p99=(Get-Percentile $endToEndValues 0.99) }
    retries = @($flatRuns | Where-Object { $_.Attempts -gt 1 }).Count
    failures = $errors.Count
    errorRate = if ($flatRuns.Count -gt 0) { [Math]::Round($errors.Count / $flatRuns.Count, 4) } else { $null }
    timeouts = @($flatRuns | Where-Object { $_.Error -eq 'execution-timeout' }).Count
    missedOccurrences = 'Not applicable: manual runs only.'
    heartbeatDelays = 'Not measured by this runner.'
    clockSkew = 'Not measured; synchronize hosts and SQL Server before interpreting timestamps.'
    executions = $flatRuns
    tenantResults = $fixtureResults
    invariantViolations = @($invariantViolations)
}
$artifact = Join-Path $resolvedOutput "workload-$runId.json"
$result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $artifact -Encoding utf8
if ($invariantViolations.Count -gt 0) { throw "Workload invariant violation(s): $($invariantViolations -join '; '). Evidence: $artifact" }
Write-Output $artifact
