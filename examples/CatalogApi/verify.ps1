param([switch]$WithCollector)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$artifacts = Join-Path $root 'artifacts/catalog-api'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$ownedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$collectorStarted = $false
$collectorProject = 'catalog-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$compose = Join-Path $PSScriptRoot 'observability/compose.yaml'
foreach ($port in @(5110, 5111) + $(if ($WithCollector) { @(4318) } else { @() })) {
    $probe = [System.Net.Sockets.TcpClient]::new()
    try {
        try { $probe.Connect('127.0.0.1', $port) } catch [System.Net.Sockets.SocketException] { }
        if ($probe.Connected) { throw "Port $port is occupied; stop the existing service first." }
    } finally { $probe.Dispose() }
}
function Start-OwnedApp([string]$dll, [string]$name, [string[]]$extra) {
    $process = Start-Process dotnet -ArgumentList (@(('"' + $dll + '"')) + $extra) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $artifacts "$name.stdout.log") -RedirectStandardError (Join-Path $artifacts "$name.stderr.log") -Environment @{ OTEL_METRIC_EXPORT_INTERVAL = '1000'; OTEL_BSP_SCHEDULE_DELAY = '1000' }
    $ownedProcesses.Add($process)
    return $process
}
function Wait-Ready([int]$port, $process) {
    for ($index = 0; $index -lt 100; $index++) {
        if ($process.HasExited) { throw "Service on $port exited before startup." }
        try { $null = Invoke-WebRequest "http://127.0.0.1:$port/health" -TimeoutSec 1; return }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Startup timeout on $port"
}
function Set-Mode([string]$mode) { $null = Invoke-RestMethod "http://127.0.0.1:5111/mode/$mode" -Method Post }
function Check-Catalog([string]$name, [int]$status, [int]$attempts, [bool]$stale = $false) {
    $response = Invoke-WebRequest 'http://127.0.0.1:5110/catalog' -SkipHttpErrorCheck -TimeoutSec 10
    $stats = Invoke-RestMethod 'http://127.0.0.1:5111/stats'
    if ([int]$response.StatusCode -ne $status -or $stats.attempts -ne $attempts) {
        throw "$name expected status=$status attempts=$attempts; got status=$($response.StatusCode) attempts=$($stats.attempts)"
    }
    if ($status -eq 200) {
        $body = $response.Content | ConvertFrom-Json
        if ($body.isStale -ne $stale -or $body.products.Count -ne 1 -or $body.products[0].id -ne 1) { throw "$name returned unexpected catalog" }
    }
    Write-Host "PASS $name status=$status attempts=$attempts stale=$stale"
}
function Wait-Settled {
    for ($index = 0; $index -lt 50; $index++) {
        $stats = Invoke-RestMethod 'http://127.0.0.1:5111/stats'
        if ($stats.active -eq 0) { return $stats }
        Start-Sleep -Milliseconds 100
    }
    throw 'Upstream handlers did not settle.'
}
try {
    foreach ($project in @('CatalogApi.csproj', 'TestUpstream/TestUpstream.csproj')) {
        dotnet build (Join-Path $PSScriptRoot $project) -c Release -warnaserror --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    if ($WithCollector) {
        $collectorStarted = $true
        docker compose -p $collectorProject -f $compose up -d
        if ($LASTEXITCODE -ne 0) { throw 'Collector startup failed' }
    }
    $upstream = Start-OwnedApp (Join-Path $PSScriptRoot 'TestUpstream/bin/Release/net10.0/TestUpstream.dll') 'upstream' @('--Logging:LogLevel:Default=Warning')
    Wait-Ready 5111 $upstream
    $api = Start-OwnedApp (Join-Path $PSScriptRoot 'bin/Release/net10.0/CatalogApi.dll') 'api' @('--Catalog:BaseUrl=http://127.0.0.1:5111/', '--Catalog:AttemptTimeoutMilliseconds=200', '--Catalog:BreakSeconds=2', '--Catalog:MaxStaleSeconds=5', '--Telemetry:Console=true', "--Telemetry:OtlpEnabled=$($WithCollector.IsPresent.ToString().ToLowerInvariant())", '--Logging:LogLevel:Default=Warning')
    Wait-Ready 5110 $api
    Set-Mode down; Check-Catalog 'cold cache' 503 3
    Set-Mode flaky; Check-Catalog 'retry recovery' 200 3
    Set-Mode missing; Check-Catalog '404 not retried or hidden by cache' 502 1
    Set-Mode invalid; Check-Catalog 'invalid JSON not retried' 502 1
    Set-Mode slow; Check-Catalog 'timeouts use last good catalog' 200 3 $true
    $stats = Wait-Settled
    if ($stats.canceled -ne 3) { throw 'Timed-out upstream requests were not canceled.' }
    Set-Mode healthy; Check-Catalog 'fresh catalog' 200 1
    Set-Mode down; Check-Catalog 'first sustained failure' 200 3 $true
    Set-Mode down; Check-Catalog 'second failure opens circuit' 200 3 $true
    Set-Mode healthy; Check-Catalog 'open circuit sends no HTTP requests' 200 0 $true
    Start-Sleep -Milliseconds 2200
    Check-Catalog 'half-open recovery' 200 1
    Set-Mode slow
    $cancelClient = [System.Net.Http.HttpClient]::new()
    $cancelSource = [System.Threading.CancellationTokenSource]::new()
    try {
        $cancelTask = $cancelClient.GetAsync('http://127.0.0.1:5110/catalog', $cancelSource.Token)
        # Wait for actual upstream admission before canceling the consumer.
        for ($index = 0; $index -lt 50; $index++) {
            if ((Invoke-RestMethod 'http://127.0.0.1:5111/stats').active -gt 0) { break }
            Start-Sleep -Milliseconds 10
        }
        $cancelSource.Cancel()
        try { $cancelTask.GetAwaiter().GetResult().Dispose(); throw 'Consumer unexpectedly completed' }
        catch [System.OperationCanceledException] { }
    } finally { $cancelSource.Dispose(); $cancelClient.Dispose() }
    $stats = Wait-Settled
    if ($stats.attempts -ne 1 -or $stats.canceled -ne 1) { throw 'Consumer cancellation was retried or left work running' }
    Write-Host 'PASS consumer cancellation: one attempt, upstream settled'
    Start-Sleep -Milliseconds 5200
    Set-Mode down; Check-Catalog 'expired cache is not returned' 503 3
    $null = Wait-Settled
    $traceLog = Get-Content (Join-Path $artifacts 'api.stdout.log') -Raw
    foreach ($span in @('resilience.pipeline', 'resilience.retry.attempt', 'resilience.fallback.execute', 'resilience.circuit_breaker')) {
        if (!$traceLog.Contains($span)) { throw "Missing console trace $span" }
    }
    if ($WithCollector) {
        Start-Sleep -Seconds 3
        $collectorLog = docker compose -p $collectorProject -f $compose logs --no-color
        $collectorLog | Set-Content (Join-Path $artifacts 'collector.log')
        if ($LASTEXITCODE -ne 0) { throw 'Could not read collector output' }
        foreach ($signal in @('resilience.pipeline', 'resilience.fallback.execute', 'catalog-upstream', 'Name: resilience.retry.count', 'Name: resilience.fallback.count')) {
            if (!(($collectorLog -join "`n").Contains($signal))) { throw "Collector did not receive $signal" }
        }
        Write-Host 'PASS real OTLP collector: spans and resilience metrics received'
    }
    Write-Host 'PASS CatalogApi: 12 HTTP scenarios, cancellation and bounded fallback cache.'
} finally {
    foreach ($process in $ownedProcesses) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
        $process.Dispose()
    }
    if ($collectorStarted) { docker compose -p $collectorProject -f $compose down }
}
