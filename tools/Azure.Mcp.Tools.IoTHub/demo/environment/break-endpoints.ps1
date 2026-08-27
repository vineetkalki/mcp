<#
.SYNOPSIS
    Breaks the four negative-test routing endpoints that azuredeploy.json provisions in a
    healthy state, so the failure modes can be reproduced and surfaced with the
    `azmcp iothub routing endpoint-diagnose` MCP tool.

.DESCRIPTION
    IoT Hub validates identity-based routing endpoints at creation time (DNS + credential
    check), so the endpoints in azuredeploy.json must point at real, authorized targets or
    the deployment fails. This script degrades those targets AFTER deployment so each
    endpoint starts failing delivery:

        noauth   -> removes the hub identity's Blob Data Contributor role on the no-auth
                    Storage account (authorization failure).
        missing  -> deletes the dedicated Service Bus namespace behind the missing-resource
                    endpoint (target resource missing).
        subres   -> deletes the dedicated queue on the main Service Bus namespace behind the
                    missing-sub-resource endpoint (sub-resource missing).
        nonet    -> disables public network access on the networking-disabled Storage
                    account (networking disabled).

    Target resource names are read from the deployment outputs, so run this against the same
    resource group and deployment used for azuredeploy.json. This script is DESTRUCTIVE
    (it deletes test-only resources and removes a role) and is intended for the temporary demo
    sample only.

.PARAMETER ResourceGroup
    Resource group that azuredeploy.json was deployed into.

.PARAMETER DeploymentName
    Name of the ARM deployment whose outputs describe the targets. Defaults to 'azuredeploy'.

.PARAMETER Only
    Optional subset of scenarios to break. One or more of: noauth, missing, subres, nonet.
    Defaults to all four.

.EXAMPLE
    ./break-endpoints.ps1 -ResourceGroup mcp-throttle

.EXAMPLE
    ./break-endpoints.ps1 -ResourceGroup mcp-throttle -Only nonet, subres
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [string] $DeploymentName = 'azuredeploy',

    [ValidateSet('noauth', 'missing', 'subres', 'nonet')]
    [string[]] $Only = @('noauth', 'missing', 'subres', 'nonet')
)

$ErrorActionPreference = 'Stop'

function Assert-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' is required but was not found on PATH."
    }
}

Assert-Command az

Write-Host "Reading deployment outputs from '$DeploymentName' in '$ResourceGroup'..." -ForegroundColor Cyan
$outputs = az deployment group show -g $ResourceGroup -n $DeploymentName `
    --query properties.outputs -o json --only-show-errors | ConvertFrom-Json
if (-not $outputs) {
    throw "Could not read outputs for deployment '$DeploymentName' in resource group '$ResourceGroup'."
}

$principalId        = $outputs.managedIdentityPrincipalId.value
$storageNoAuth      = $outputs.storageNoAuthAccount.value
$storageNoNet       = $outputs.storageNoNetAccount.value
$breakSbNamespace   = $outputs.breakServiceBusNamespace.value
$breakQueue         = $outputs.breakQueue.value
$mainSbNamespace    = $outputs.serviceBusNamespace.value

# noauth: remove the identity's Blob Data Contributor role so delivery fails authorization.
if ($Only -contains 'noauth') {
    Write-Host ""
    Write-Host "[noauth] Removing Blob Data Contributor on '$storageNoAuth'..." -ForegroundColor Cyan
    $scope = az storage account show -g $ResourceGroup -n $storageNoAuth --query id -o tsv --only-show-errors
    az role assignment delete --assignee $principalId --role "Storage Blob Data Contributor" --scope $scope --only-show-errors | Out-Null
    Write-Host "[noauth] Done." -ForegroundColor Green
}

# missing: delete the dedicated Service Bus namespace so the endpoint target no longer resolves.
if ($Only -contains 'missing') {
    Write-Host ""
    Write-Host "[missing] Deleting Service Bus namespace '$breakSbNamespace'..." -ForegroundColor Cyan
    az servicebus namespace delete -g $ResourceGroup --name $breakSbNamespace --only-show-errors | Out-Null
    Write-Host "[missing] Done." -ForegroundColor Green
}

# subres: delete only the dedicated queue on the main namespace, leaving the namespace intact.
if ($Only -contains 'subres') {
    Write-Host ""
    Write-Host "[subres] Deleting queue '$breakQueue' on '$mainSbNamespace'..." -ForegroundColor Cyan
    az servicebus queue delete -g $ResourceGroup --namespace-name $mainSbNamespace --name $breakQueue --only-show-errors | Out-Null
    Write-Host "[subres] Done." -ForegroundColor Green
}

# nonet: disable public network access so IoT Hub can no longer reach the account.
if ($Only -contains 'nonet') {
    Write-Host ""
    Write-Host "[nonet] Disabling public network access on '$storageNoNet'..." -ForegroundColor Cyan
    az storage account update -g $ResourceGroup -n $storageNoNet --public-network-access Disabled --default-action Deny --only-show-errors | Out-Null
    Write-Host "[nonet] Done." -ForegroundColor Green
}

Write-Host ""
Write-Host "Endpoints broken. Send traffic to exercise them, then wait 2-5 minutes for metrics:" -ForegroundColor Green
Write-Host "  ./send-messages.ps1 -HubName <hub> -ResourceGroup $ResourceGroup -Targets noauth,missingresource,missingsubresource,nonet -Count 200 -Parallel 8" -ForegroundColor DarkGray
Write-Host "  azmcp iothub routing endpoint-diagnose --hub-name <hub> --resource-group $ResourceGroup" -ForegroundColor DarkGray
