param([int]$Requests = 256, [int]$Concurrency = 32)
$ErrorActionPreference = 'Stop'
if ($Requests -lt 16 -or $Requests -gt 100000 -or $Concurrency -lt 8 -or $Concurrency -gt 256) {
    throw 'Requests: 16..100000; Concurrency: 8..256.'
}
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outputDirectory = Join-Path $projectRoot 'artifacts/load-check'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
dotnet build (Join-Path $projectRoot 'ResilienceLab.slnx') -c Release --nologo -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$probe = [System.Net.Sockets.TcpClient]::new()
try {
    try { $probe.Connect('127.0.0.1', 5099) } catch [System.Net.Sockets.SocketException] { }
    if ($probe.Connected) { throw 'Port 5099 is occupied. Stop that API before running load checks.' }
} finally { $probe.Dispose() }
$apiDll = Join-Path $projectRoot 'samples/FaultApi/bin/Release/net10.0/FaultApi.dll'
$apiProcess = Start-Process dotnet -ArgumentList @(('"' + $apiDll + '"'), '--Logging:LogLevel:Default=Warning') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $outputDirectory 'server.log') -RedirectStandardError (Join-Path $outputDirectory 'server-errors.log')
try {
    $ready = $false
    for ($index = 0; $index -lt 50; $index++) {
        if ($apiProcess.HasExited) { throw 'API exited before startup; inspect server-errors.log.' }
        try { $null = Invoke-WebRequest 'http://127.0.0.1:5099/health' -TimeoutSec 1; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'API startup timed out' }
    $runnerDll = Join-Path $PSScriptRoot 'bin/Release/net10.0/LoadCheck.dll'
    dotnet $runnerDll $Requests $Concurrency $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Load checks failed; inspect report.json and server logs.' }
    Write-Host "Report: $outputDirectory/report.md"
} finally {
    # The DLL was launched directly; stop only this exact owned process.
    if (!$apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id; $apiProcess.WaitForExit() }
}
