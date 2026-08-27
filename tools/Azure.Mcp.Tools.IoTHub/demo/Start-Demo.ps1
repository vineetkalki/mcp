[CmdletBinding()]
param(
    [int] $Port = 4173,
    [string] $Subscription = 'iot-sub-1038',
    [string] $ResourceGroup = 'mcp-throttle',
    [string] $HubName = 'iothrottlehubs3576f',
    [string] $CodePath
)

$ErrorActionPreference = 'Stop'

$node = Get-Command node -ErrorAction Stop
$pwsh = Get-Command pwsh -ErrorAction Stop
$serverPath = Join-Path $PSScriptRoot 'server.mjs'
$pidPath = Join-Path $PSScriptRoot '.demo-server.pid'
$stdoutPath = Join-Path $PSScriptRoot '.demo-server.out.log'
$stderrPath = Join-Path $PSScriptRoot '.demo-server.err.log'
$actionStatePath = Join-Path $PSScriptRoot '.demo-action.json'
$instanceToken = [guid]::NewGuid().ToString('N')

if (Test-Path $actionStatePath) {
    $actionState = Get-Content $actionStatePath -Raw | ConvertFrom-Json
    $actionProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($actionState.pid)" -ErrorAction SilentlyContinue
    if ($actionProcess -and
        $actionProcess.CommandLine -like "*$($actionState.actionScript)*" -and
        $actionProcess.CommandLine -like "*$($actionState.actionId)*" -and
        $actionProcess.CommandLine -like "*$($actionState.instanceToken)*") {
        throw "Demo action '$($actionState.actionId)' is still running with process ID $($actionState.pid)."
    }
    Remove-Item $actionStatePath -Force
}

if (Test-Path $pidPath) {
    $state = Get-Content $pidPath -Raw | ConvertFrom-Json
    $existingProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($state.pid)" -ErrorAction SilentlyContinue
    if ($existingProcess -and
        $existingProcess.CommandLine -like "*$serverPath*" -and
        $existingProcess.CommandLine -like "*$($state.instanceToken)*") {
        throw "The demo server is already running with process ID $($state.pid). Run Stop-Demo.ps1 first."
    }
    Remove-Item $pidPath -Force
}

$portProbe = [System.Net.Sockets.TcpClient]::new()
try {
    $connection = $portProbe.ConnectAsync('127.0.0.1', $Port)
    if ($connection.Wait(250) -and $portProbe.Connected) {
        throw "Port $Port is already in use. Choose another port with -Port."
    }
}
finally {
    $portProbe.Dispose()
}

if ([string]::IsNullOrWhiteSpace($CodePath)) {
    $codeCommand = Get-Command code -ErrorAction Stop
    $installRoot = Split-Path (Split-Path $codeCommand.Source -Parent) -Parent
    $CodePath = @(
        (Join-Path $installRoot 'Code.exe'),
        (Join-Path $installRoot 'Code - Insiders.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not (Test-Path $CodePath)) {
    throw "Native VS Code executable not found: $CodePath"
}

$agentTemplate = Join-Path $PSScriptRoot 'iothub-routing-demo-temp.agent.md'
$agentDirectory = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '..\\.github\\agents'
$agentDirectory = [IO.Path]::GetFullPath($agentDirectory)
$agentPath = Join-Path $agentDirectory 'iothub-routing-demo-temp.agent.md'
if (-not (Test-Path $agentDirectory)) {
    New-Item -Path $agentDirectory -ItemType Directory -Force | Out-Null
}
if (Test-Path $agentPath) {
    $templateHash = (Get-FileHash $agentTemplate).Hash
    $existingHash = (Get-FileHash $agentPath).Hash
    if ($templateHash -ne $existingHash) {
        throw "Refusing to overwrite existing custom agent: $agentPath"
    }
}
else {
    Copy-Item $agentTemplate $agentPath
}
$agentHash = (Get-FileHash $agentPath).Hash

$arguments = @(
    $serverPath,
    '--port', $Port,
    '--subscription', $Subscription,
    '--resource-group', $ResourceGroup,
    '--hub-name', $HubName,
    '--instance-token', $instanceToken,
    '--code-path', "`"$CodePath`"",
    '--pwsh-path', "`"$($pwsh.Source)`""
)

$process = Start-Process `
    -FilePath $node.Source `
    -ArgumentList $arguments `
    -WorkingDirectory $PSScriptRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -PassThru

@{
    pid = $process.Id
    port = $Port
    instanceToken = $instanceToken
    serverPath = $serverPath
    agentPath = $agentPath
    agentHash = $agentHash
} | ConvertTo-Json | Set-Content $pidPath
$url = "http://127.0.0.1:$Port"

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
            $serverError = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
            throw "The demo server exited during startup. $serverError"
        }
        try {
            $response = Invoke-WebRequest "$url/api/health" -UseBasicParsing -TimeoutSec 2
            $health = $response.Content | ConvertFrom-Json
            if ($response.StatusCode -eq 200 -and $health.instanceToken -eq $instanceToken) {
                $ready = $true
                break
            }
        }
        catch {
        }
    }

    if (-not $ready) {
        throw "The demo server did not become ready. See $stderrPath."
    }

    $browserCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )
    $browser = $browserCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($browser) {
        Start-Process -FilePath $browser -ArgumentList '--new-window', '--start-maximized', $url
    }
    else {
        Start-Process $url
    }

    Write-Host "IoT Hub routing demo started at $url" -ForegroundColor Green
    Write-Host "Local server PID: $($process.Id)"
    Write-Host "Stop it with: .\tools\Azure.Mcp.Tools.IoTHub\demo\Stop-Demo.ps1"
}
catch {
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
        Stop-Process -Id $process.Id
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
    if (Test-Path $agentPath) {
        $currentAgentHash = (Get-FileHash $agentPath).Hash
        if ($currentAgentHash -eq $agentHash) {
            Remove-Item $agentPath -Force
        }
    }
    throw
}
