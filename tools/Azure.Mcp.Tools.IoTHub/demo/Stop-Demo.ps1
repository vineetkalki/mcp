[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pidPath = Join-Path $PSScriptRoot '.demo-server.pid'
$actionStatePath = Join-Path $PSScriptRoot '.demo-action.json'

function Stop-ProcessTree([int] $RootProcessId) {
    $allProcesses = Get-CimInstance Win32_Process
    $children = @($allProcesses | Where-Object ParentProcessId -eq $RootProcessId)
    foreach ($child in $children) {
        Stop-ProcessTree $child.ProcessId
    }
    if (Get-Process -Id $RootProcessId -ErrorAction SilentlyContinue) {
        Stop-Process -Id $RootProcessId
    }
}

if (-not (Test-Path $pidPath)) {
    Write-Host 'The demo server is not running.' -ForegroundColor Yellow
    exit 0
}

$state = Get-Content $pidPath -Raw | ConvertFrom-Json
$serverPid = [int]$state.pid
$processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $serverPid" -ErrorAction SilentlyContinue
if ($processInfo) {
    try {
        $actionStatus = Invoke-RestMethod "http://127.0.0.1:$($state.port)/api/action/status" -TimeoutSec 2
        if ($actionStatus.running) {
            throw "Demo action '$($actionStatus.action.id)' is still running. Wait for it to finish before stopping the demo."
        }
    }
    catch {
        if ($_.Exception.Message -like 'Demo action * is still running*') {
            throw
        }
        if (Test-Path $actionStatePath) {
            $actionState = Get-Content $actionStatePath -Raw | ConvertFrom-Json
            $actionProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($actionState.pid)" -ErrorAction SilentlyContinue
            $isExpectedAction =
                $actionProcess -and
                $actionProcess.CommandLine -like "*$($actionState.actionScript)*" -and
                $actionProcess.CommandLine -like "*$($actionState.actionId)*" -and
                $actionProcess.CommandLine -like "*$($actionState.instanceToken)*"
            if ($isExpectedAction) {
                throw "Unable to query action status, and recorded demo action '$($actionState.actionId)' is still running."
            }
            Remove-Item $actionStatePath -Force -ErrorAction SilentlyContinue
        }
    }

    $isExpectedProcess =
        $processInfo.CommandLine -like "*$($state.serverPath)*" -and
        $processInfo.CommandLine -like "*$($state.instanceToken)*"
    if (-not $isExpectedProcess) {
        Remove-Item $pidPath -Force
        throw "Refusing to stop process $serverPid because it is not the recorded demo server."
    }

    Stop-Process -Id $serverPid
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (-not (Get-Process -Id $serverPid -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Id $serverPid -ErrorAction SilentlyContinue) {
        throw "Demo server process $serverPid did not stop within 5 seconds."
    }
}
elseif (Test-Path $actionStatePath) {
    $actionState = Get-Content $actionStatePath -Raw | ConvertFrom-Json
    $actionProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($actionState.pid)" -ErrorAction SilentlyContinue
    $isExpectedAction =
        $actionProcess -and
        $actionProcess.CommandLine -like "*$($actionState.actionScript)*" -and
        $actionProcess.CommandLine -like "*$($actionState.actionId)*" -and
        $actionProcess.CommandLine -like "*$($actionState.instanceToken)*"
    if ($isExpectedAction) {
        Stop-ProcessTree $actionState.pid
    }
    Remove-Item $actionStatePath -Force -ErrorAction SilentlyContinue
}

if ($state.agentPath -and (Test-Path $state.agentPath)) {
    $canRemoveAgent =
        $state.agentHash -and
        ((Get-FileHash $state.agentPath).Hash -eq $state.agentHash)
    if ($canRemoveAgent) {
        Remove-Item $state.agentPath -Force
    }
}

Remove-Item $pidPath -Force
Write-Host "Stopped demo server process $serverPid." -ForegroundColor Green
