$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outputDirectory = Join-Path $projectRoot 'artifacts/package-consumer'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
foreach ($packageProject in @('src/ResilienceLab', 'src/ResilienceLab.Http')) {
    dotnet pack (Join-Path $projectRoot $packageProject) -c Release -o (Join-Path $projectRoot 'artifacts/packages') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package generation failed' }
}
$consumerProject = Join-Path $PSScriptRoot 'PackageConsumer.csproj'
# A dedicated cache ensures we consume the packed assemblies, not the global cache.
$cacheDirectory = Join-Path $outputDirectory ('cache-' + [Guid]::NewGuid().ToString('N'))
dotnet restore $consumerProject --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --packages $cacheDirectory --force --nologo
if ($LASTEXITCODE -ne 0) { throw 'Consumer package restore failed' }
dotnet build $consumerProject -c Release --no-restore -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { throw 'Consumer build failed' }
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($package in @('ResilienceLab/0.5.0', 'ResilienceLab.Http/0.5.0')) {
    if ($assets.libraries[$package].type -ne 'package') { throw "$package is not a NuGet dependency" }
}
$probe = [System.Net.Sockets.TcpClient]::new()
try {
    try { $probe.Connect('127.0.0.1', 5101) } catch [System.Net.Sockets.SocketException] { }
    if ($probe.Connected) { throw 'Port 5101 is occupied. Stop the consumer before verification.' }
} finally { $probe.Dispose() }
$consumerDll = Join-Path $PSScriptRoot 'bin/Release/net10.0/PackageConsumer.dll'
$consumerProcess = Start-Process dotnet -ArgumentList @(('"' + $consumerDll + '"'), '--Logging:LogLevel:Default=Warning') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $outputDirectory 'stdout.log') -RedirectStandardError (Join-Path $outputDirectory 'stderr.log')
try {
    $ready = $false
    for ($index = 0; $index -lt 50; $index++) {
        if ($consumerProcess.HasExited) { throw 'Consumer exited before startup' }
        try { $null = Invoke-WebRequest 'http://127.0.0.1:5101/health' -TimeoutSec 1; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'Startup timed out' }
    $cases = @(
        @{ Mode = 'flaky'; Attempts = 3; Body = 'fresh-value' },
        @{ Mode = 'healthy'; Attempts = 1; Body = 'fresh-value' },
        @{ Mode = 'slow'; Attempts = 1; Body = 'cached-value' },
        @{ Mode = 'down'; Attempts = 3; Body = 'cached-value' },
        @{ Mode = 'down'; Attempts = 3; Body = 'cached-value' },
        @{ Mode = 'healthy'; Attempts = 0; Body = 'cached-value' }
    )
    foreach ($case in $cases) {
        $result = Invoke-RestMethod "http://127.0.0.1:5101/catalog/$($case.Mode)" -TimeoutSec 10
        if ($result.attempts -ne $case.Attempts -or $result.body -ne $case.Body) {
            throw "Unexpected $($case.Mode) result: $($result | ConvertTo-Json -Compress)"
        }
        Write-Host "PASS $($case.Mode): attempts=$($result.attempts), body=$($result.body)"
    }
    $traceLog = Get-Content -LiteralPath (Join-Path $outputDirectory 'stdout.log') -Raw
    foreach ($span in @('pipeline', 'retry.attempt', 'http_retry', 'timeout', 'fallback.execute', 'circuit_breaker')) {
        if (!$traceLog.Contains("[traza] resilience.$span ")) { throw "Missing span: $span" }
    }
    Write-Host 'PASS NuGet consumer: six HTTP checks, DI, pipeline and traces.'
} finally {
    if (!$consumerProcess.HasExited) { Stop-Process -Id $consumerProcess.Id; $consumerProcess.WaitForExit() }
}
