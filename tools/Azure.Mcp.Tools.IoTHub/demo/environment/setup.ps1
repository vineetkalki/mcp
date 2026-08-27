<#
.SYNOPSIS
    Provisions the temporary demo environment end to end: deploys azuredeploy.json and then
    runs break-endpoints.ps1, so the negative-test routing endpoints start failing delivery.

.DESCRIPTION
    azuredeploy.json intentionally provisions its four negative-test endpoints in a healthy
    state (IoT Hub validates identity-based endpoints at creation time, so their targets must
    exist and be authorized first). The ARM deployment therefore CANNOT break those endpoints
    itself. This script runs both steps in order:

        1. az deployment group create  -> provisions the hub and all routing endpoints.
        2. break-endpoints.ps1          -> degrades the four negative-test targets so the
                                           authorization / missing-resource / missing-sub-resource /
                                           networking-disabled failure modes reproduce.

    After it finishes, the hub name (the 'iotHubName' deployment output) is printed so you can
    send traffic with send-messages.ps1 and inspect health with the MCP tools.

.PARAMETER ResourceGroup
    Resource group to deploy into. Created if it does not already exist.

.PARAMETER Location
    Azure region for the resource group and resources. Defaults to 'westus'. Ignored when the
    resource group already exists.

.PARAMETER Subscription
    Optional subscription id or name to target. Defaults to the current az context.

.PARAMETER DeploymentName
    Name of the ARM deployment. Defaults to 'azuredeploy'. break-endpoints.ps1 reads its outputs.

.PARAMETER BaseName
    Short prefix for all resource names (passed to azuredeploy.json's baseName parameter).

.PARAMETER RemoteEventHubSubscriptionId
    Subscription id of the remote (cross-subscription) Event Hub used by the 'remote' route.
    Overrides the template default. The hub's managed identity must be granted 'Azure Event
    Hubs Data Sender' on this Event Hub separately - that grant is outside this deployment.

.PARAMETER RemoteEventHubResourceGroup
    Resource group of the remote Event Hub namespace. Overrides the template default.

.PARAMETER RemoteEventHubNamespace
    Remote Event Hubs namespace name. Overrides the template default.

.PARAMETER RemoteEventHubName
    Remote Event Hub (entity) name. Overrides the template default.

.PARAMETER Only
    Optional subset of negative-test scenarios to break. One or more of: noauth, missing,
    subres, nonet. Defaults to all four.

.PARAMETER SkipBreak
    Deploy only; do not run break-endpoints.ps1. Use when you want the endpoints left healthy.

.EXAMPLE
    ./setup.ps1 -ResourceGroup mcp-throttle

.EXAMPLE
    ./setup.ps1 -ResourceGroup mcp-throttle -Subscription iot-sub-1038 -Location eastus

.EXAMPLE
    ./setup.ps1 -ResourceGroup mcp-throttle -Only nonet, subres
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [string] $Location = 'westus',

    [string] $Subscription,

    [string] $DeploymentName = 'azuredeploy',

    [string] $BaseName = 'iothrottle',

    [string] $RemoteEventHubSubscriptionId,

    [string] $RemoteEventHubResourceGroup,

    [string] $RemoteEventHubNamespace,

    [string] $RemoteEventHubName,

    [ValidateSet('noauth', 'missing', 'subres', 'nonet')]
    [string[]] $Only = @('noauth', 'missing', 'subres', 'nonet'),

    [switch] $SkipBreak
)

$ErrorActionPreference = 'Stop'

function Assert-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' is required but was not found on PATH."
    }
}

Assert-Command az

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$templatePath = Join-Path $scriptDir 'azuredeploy.json'
$breakScript = Join-Path $scriptDir 'break-endpoints.ps1'

if (-not (Test-Path $templatePath)) { throw "Template not found: $templatePath" }
if (-not $SkipBreak -and -not (Test-Path $breakScript)) { throw "Break script not found: $breakScript" }

# Common subscription argument applied to every az call so the whole run stays on one context.
$subArgs = @()
if ($Subscription) { $subArgs = @('--subscription', $Subscription) }

Write-Host "Ensuring resource group '$ResourceGroup' exists (location '$Location')..." -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location @subArgs --only-show-errors | Out-Null

# Only pass remote Event Hub parameters the caller supplied; unset ones keep the template defaults.
$deployParams = @("baseName=$BaseName")
if ($RemoteEventHubSubscriptionId) { $deployParams += "remoteEventHubSubscriptionId=$RemoteEventHubSubscriptionId" }
if ($RemoteEventHubResourceGroup) { $deployParams += "remoteEventHubResourceGroup=$RemoteEventHubResourceGroup" }
if ($RemoteEventHubNamespace) { $deployParams += "remoteEventHubNamespace=$RemoteEventHubNamespace" }
if ($RemoteEventHubName) { $deployParams += "remoteEventHubName=$RemoteEventHubName" }

Write-Host ""
Write-Host "[1/2] Deploying '$DeploymentName' from azuredeploy.json..." -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --name $DeploymentName `
    --template-file $templatePath `
    --parameters @deployParams `
    @subArgs --only-show-errors | Out-Null

$hubName = az deployment group show -g $ResourceGroup -n $DeploymentName `
    --query properties.outputs.iotHubName.value -o tsv @subArgs --only-show-errors
if ([string]::IsNullOrWhiteSpace($hubName)) {
    throw "Deployment '$DeploymentName' succeeded but no iotHubName output was found."
}
Write-Host "[1/2] Deployed. IoT Hub: '$hubName'." -ForegroundColor Green

if ($SkipBreak) {
    Write-Host ""
    Write-Host "SkipBreak set - negative-test endpoints left healthy." -ForegroundColor Yellow
    Write-Host "Run break-endpoints.ps1 later to degrade them." -ForegroundColor DarkGray
    exit 0
}

Write-Host ""
Write-Host "[2/2] Breaking negative-test endpoints ($($Only -join ', '))..." -ForegroundColor Cyan
& $breakScript -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName -Only $Only
Write-Host "[2/2] Break steps complete." -ForegroundColor Green

Write-Host ""
Write-Host "Setup complete. Next steps:" -ForegroundColor Green
Write-Host "  ./send-messages.ps1 -HubName $hubName -ResourceGroup $ResourceGroup -Mode Burst" -ForegroundColor DarkGray
Write-Host "  azmcp iothub routing endpoint-health --hub-name $hubName --resource-group $ResourceGroup" -ForegroundColor DarkGray
