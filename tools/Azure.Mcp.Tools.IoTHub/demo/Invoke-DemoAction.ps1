[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provision', 'Break', 'Standard', 'Burst')]
    [string] $Action,

    [Parameter(Mandatory)]
    [string] $Subscription,

    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [string] $HubName,

    [Parameter(Mandatory)]
    [string] $InstanceToken,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$sampleDirectory = Join-Path $PSScriptRoot 'environment'
$setupScript = Join-Path $sampleDirectory 'setup.ps1'
$breakScript = Join-Path $sampleDirectory 'break-endpoints.ps1'
$sendScript = Join-Path $sampleDirectory 'send-messages.ps1'
$templatePath = Join-Path $sampleDirectory 'azuredeploy.json'

foreach ($path in @($setupScript, $breakScript, $sendScript, $templatePath)) {
    if (-not (Test-Path $path)) {
        throw "Required demo asset not found: $path"
    }
}

if ($DryRun) {
    $description = switch ($Action) {
        'Provision' { "setup.ps1 -Subscription '$Subscription' -ResourceGroup '$ResourceGroup' -DeploymentName azuredeploy -SkipBreak (template: $templatePath)" }
        'Break' { "break-endpoints.ps1 -ResourceGroup '$ResourceGroup' -DeploymentName azuredeploy" }
        'Standard' { "send-messages.ps1 -HubName '$HubName' -ResourceGroup '$ResourceGroup' -Mode Standard -Targets all" }
        'Burst' { "send-messages.ps1 -HubName '$HubName' -ResourceGroup '$ResourceGroup' -Mode Burst -Targets all" }
    }
    Write-Output "DRY RUN: $description"
    exit 0
}

az account set --subscription $Subscription
if ($LastExitCode -ne 0) {
    throw "Unable to select Azure subscription '$Subscription'."
}

switch ($Action) {
    'Provision' {
        Write-Host "Provisioning healthy demo resources from '$templatePath'..." -ForegroundColor Cyan
        & $setupScript `
            -Subscription $Subscription `
            -ResourceGroup $ResourceGroup `
            -DeploymentName 'azuredeploy' `
            -SkipBreak
    }
    'Break' {
        Write-Host "Breaking the four negative-test destinations..." -ForegroundColor Cyan
        & $breakScript `
            -ResourceGroup $ResourceGroup `
            -DeploymentName 'azuredeploy'
    }
    'Standard' {
        Write-Host "Sending Standard traffic to all routing endpoints..." -ForegroundColor Cyan
        & $sendScript `
            -HubName $HubName `
            -ResourceGroup $ResourceGroup `
            -Mode Standard `
            -Targets all
    }
    'Burst' {
        Write-Host "Sending Burst traffic to all routing endpoints..." -ForegroundColor Cyan
        & $sendScript `
            -HubName $HubName `
            -ResourceGroup $ResourceGroup `
            -Mode Burst `
            -Targets all
    }
}

if ($LastExitCode -ne 0) {
    throw "Demo action '$Action' failed with exit code $LastExitCode."
}

Write-Host "Demo action '$Action' completed." -ForegroundColor Green
