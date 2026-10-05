param([switch]$Tracing)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
dotnet build (Join-Path $PSScriptRoot 'FaultApi.csproj') -c Release --nologo -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$logDirectory = Join-Path $projectRoot 'artifacts/fault-api'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$healthUrl = 'http://127.0.0.1:5099/health'
try {
    $null = Invoke-WebRequest $healthUrl -TimeoutSec 1
    throw 'Port 5099 already hosts the sample. Stop it before verification.'
} catch {
    if ($_.Exception.Message -like 'Port 5099*') { throw }
}
$apiArguments = @('run', '--project', (Join-Path $PSScriptRoot 'FaultApi.csproj'), '-c', 'Release', '--no-build')
if ($Tracing) { $apiArguments += @('--', '--Tracing:Console=true') }
$apiProcess = Start-Process dotnet -ArgumentList $apiArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logDirectory 'stdout.log') -RedirectStandardError (Join-Path $logDirectory 'stderr.log')
try {
    $ready = $false
    for ($index = 0; $index -lt 50; $index++) {
        if ($apiProcess.HasExited) { throw 'API exited before startup; inspect artifacts/fault-api.' }
        try { $null = Invoke-WebRequest $healthUrl -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'API did not start' }
    $cases = @(
        @{ Name = 'retry'; Status = 200; Attempts = 3 },
        @{ Name = 'timeout'; Status = 504; Attempts = 1 },
        @{ Name = 'fallback'; Status = 200; Attempts = 3 },
        @{ Name = 'circuit'; Status = 502; Attempts = 3 },
        @{ Name = 'circuit'; Status = 502; Attempts = 3 },
        @{ Name = 'circuit'; Status = 503; Attempts = 0 }
    )
    foreach ($case in $cases) {
        $response = Invoke-WebRequest "http://127.0.0.1:5099/demo/$($case.Name)" -SkipHttpErrorCheck -TimeoutSec 10
        $body = $response.Content | ConvertFrom-Json
        if ([int]$response.StatusCode -ne $case.Status -or $body.attempts -ne $case.Attempts) {
            throw "Unexpected result for $($case.Name): $($response.Content)"
        }
        if ($case.Name -eq 'fallback' -and $body.body -ne 'cached-value') { throw 'Fallback value missing' }
        Write-Host "PASS $($case.Name): HTTP $($case.Status), attempts=$($body.attempts)"
    }
    Write-Host 'Six local HTTP checks passed.'
    if ($Tracing) {
        $traceLog = Get-Content -LiteralPath (Join-Path $logDirectory 'stdout.log') -Raw
        if ($traceLog -notmatch '\[traza\] resilience.http_retry.attempt' -or $traceLog -notmatch '\[traza\] resilience.timeout') {
            throw 'Expected HTTP and timeout spans are missing'
        }
        Write-Host 'PASS trace output: HTTP attempts and timeout spans captured.'
    }
} finally {
    # Stop only the process tree started by this script.
    taskkill /PID $apiProcess.Id /T /F | Out-Null
}
