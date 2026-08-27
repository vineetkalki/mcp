<#
.SYNOPSIS
    Sends device-to-cloud (D2C) messages to an IoT Hub device to exercise temporary demo
    routing endpoints, so routing-endpoint throttling can be reproduced and then
    surfaced with the `azmcp iothub routing endpoint-health` MCP tool.

.DESCRIPTION
    Each message carries an application property named 'target'. The IoT Hub routes
    provisioned by azuredeploy.json select an endpoint based on that property:

        target = 'eventhub'  -> Event Hubs endpoint (same subscription)
        target = 'remote'    -> Event Hubs endpoint in another subscription
        target = 'sbqueue'   -> Service Bus queue endpoint
        target = 'sbtopic'   -> Service Bus topic endpoint
        target = 'storage'   -> Blob Storage container endpoint (Table Storage isn't supported by IoT Hub routing)
        target = 'cosmos'    -> Cosmos DB SQL container endpoint
        target = 'noauth'    -> Storage endpoint whose account exists but the hub identity has no role (authorization failure)
        target = 'missingresource'    -> Service Bus queue endpoint whose namespace does not exist (resource missing)
        target = 'missingsubresource' -> Service Bus queue endpoint whose namespace exists but the queue does not (sub-resource missing)
        target = 'nonet'     -> Storage endpoint whose account has public network access disabled (networking disabled)
        target = 'all'       -> every endpoint (all routes match)

    Messages are sent over the IoT Hub HTTPS D2C REST endpoint using a per-device SAS
    token. Downstream routing endpoints authenticate with the hub's managed identity
    (no local auth) - only the device-to-hub leg uses a device SAS token, which is
    inherent to IoT Hub device messaging.

    Tune which endpoints are hit with -Targets and the volume with -Count / -Parallel.
    To reach routing throttle limits, use a large -Count with -Parallel > 1.

.PARAMETER HubName
    Name of the IoT Hub (the 'iotHubName' output of azuredeploy.json).

.PARAMETER ResourceGroup
    Resource group containing the IoT Hub.

.PARAMETER DeviceId
    Device id to send from. Created automatically if it does not exist.

.PARAMETER Targets
    One or more of: eventhub, remote, sbqueue, sbtopic, storage, cosmos, noauth,
    missingresource, missingsubresource, nonet, all. The last four deliberately fail
    delivery (authorization failure, missing resource, missing sub-resource, networking
    disabled) once break-endpoints.ps1 has been run. Each selected target gets -Count messages.

.PARAMETER Count
    Number of messages to send per target. Increase to hit throttle limits.

.PARAMETER Parallel
    Number of concurrent senders (PowerShell 7 parallel throttle limit).

.PARAMETER IntervalMs
    Optional delay in milliseconds between sends within a single runspace.

.PARAMETER Mode
    High-level intent that presets the volume knobs (override any preset by passing the knob explicitly):
      Standard - a gentle, steady load that should NOT trigger routing throttling.
      Burst    - a large, high-concurrency burst designed to trigger downstream throttling.
      Reset    - send nothing; clear queued Service Bus backlog (test-only, delete+recreate) and
                 wait for endpoints to recover naturally. Does NOT scale any resource. Event Hubs,
                 Storage, and Cosmos have no drainable backlog and recover on cool-down.
      Custom   - (default) use the -Count/-Parallel/-PayloadBytes/-IntervalMs values as given.

.PARAMETER ResetTimeoutSeconds
    Reset mode only: maximum seconds to wait for all endpoints to recover.

.PARAMETER ResetPollSeconds
    Reset mode only: seconds between routing endpoint health polls.

.EXAMPLE
    # Standard load - normal traffic, no throttling expected
    ./send-messages.ps1 -HubName iothrottlehubab12cd -ResourceGroup my-rg -Mode Standard

.EXAMPLE
    # Burst load - hammer every endpoint to trigger throttling
    ./send-messages.ps1 -HubName iothrottlehubab12cd -ResourceGroup my-rg -Mode Burst

.EXAMPLE
    # Reset - stop load and wait for previously throttled endpoints to recover
    ./send-messages.ps1 -HubName iothrottlehubab12cd -ResourceGroup my-rg -Mode Reset

.EXAMPLE
    ./send-messages.ps1 -HubName iothrottlehubab12cd -ResourceGroup my-rg -Targets eventhub -Count 1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $HubName,

    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [ValidateSet('Standard', 'Burst', 'Reset', 'Custom')]
    [string] $Mode = 'Custom',

    [string] $DeviceId = 'throttle-test-device',

    [ValidateSet('eventhub', 'remote', 'sbqueue', 'sbtopic', 'storage', 'cosmos', 'noauth', 'missingresource', 'missingsubresource', 'nonet', 'all')]
    [string[]] $Targets = @('all'),

    [ValidateRange(1, 10000000)]
    [int] $Count = 1,

    [ValidateRange(1, 256)]
    [int] $Parallel = 1,

    [ValidateRange(0, 60000)]
    [int] $IntervalMs = 0,

    [ValidateRange(0, 255000)]
    [int] $PayloadBytes = 0,

    [ValidateRange(10, 3600)]
    [int] $ResetTimeoutSeconds = 300,

    [ValidateRange(5, 300)]
    [int] $ResetPollSeconds = 15
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ is required (uses ForEach-Object -Parallel). Current: $($PSVersionTable.PSVersion)."
}

# When -Mode isn't supplied and we have a console, prompt for one of the three intents.
if (-not $PSBoundParameters.ContainsKey('Mode') -and -not [Console]::IsInputRedirected) {
    while ($true) {
        Write-Host ""
        Write-Host "Select what to run:" -ForegroundColor Cyan
        Write-Host "  1) Standard - normal load (should not throttle)"
        Write-Host "  2) Burst    - large burst to trigger throttling"
        Write-Host "  3) Reset    - clear Service Bus backlog and wait for endpoints to recover"
        Write-Host "  q) Quit"
        $choice = (Read-Host "Enter choice [1-3 or q]").Trim().ToLower()
        if ($choice -eq '1') { $Mode = 'Standard'; break }
        elseif ($choice -eq '2') { $Mode = 'Burst'; break }
        elseif ($choice -eq '3') { $Mode = 'Reset'; break }
        elseif ($choice -eq 'q') { Write-Host "Cancelled." -ForegroundColor Yellow; exit 0 }
        else { Write-Host "Invalid choice '$choice'. Try again." -ForegroundColor Yellow }
    }
}

# Mode presets pick an intent; any knob the caller passed explicitly still wins over the preset.
switch ($Mode) {
    'Standard' {
        if (-not $PSBoundParameters.ContainsKey('Count')) { $Count = 50 }
        if (-not $PSBoundParameters.ContainsKey('Parallel')) { $Parallel = 4 }
        if (-not $PSBoundParameters.ContainsKey('IntervalMs')) { $IntervalMs = 50 }
        if (-not $PSBoundParameters.ContainsKey('PayloadBytes')) { $PayloadBytes = 0 }
    }
    'Burst' {
        if (-not $PSBoundParameters.ContainsKey('Count')) { $Count = 8000 }
        if (-not $PSBoundParameters.ContainsKey('Parallel')) { $Parallel = 32 }
        if (-not $PSBoundParameters.ContainsKey('IntervalMs')) { $IntervalMs = 0 }
        if (-not $PSBoundParameters.ContainsKey('PayloadBytes')) { $PayloadBytes = 200000 }
    }
}

function Assert-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' is required but was not found on PATH."
    }
}

Assert-Command az

# Registry api-version for device create/get; messaging uses its own version further down.
$registryApiVersion = '2021-04-12'

function New-HubSasToken {
    param(
        [string] $ResourceUri,
        [string] $Key,
        [string] $PolicyName,
        [int] $TtlSeconds = 3600
    )
    $encodedUri = [uri]::EscapeDataString($ResourceUri)
    $expiry = [System.DateTimeOffset]::UtcNow.AddSeconds($TtlSeconds).ToUnixTimeSeconds()
    $toSign = "$encodedUri`n$expiry"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($Key))
    try {
        $sig = [Convert]::ToBase64String($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($toSign)))
    }
    finally {
        $hmac.Dispose()
    }
    $token = "SharedAccessSignature sr=$encodedUri&sig=$([uri]::EscapeDataString($sig))&se=$expiry"
    if ($PolicyName) { $token += "&skn=$PolicyName" }
    return $token
}

# Poll the IoT Hub routing endpoint health until no endpoint is throttled/unhealthy (or timeout).
function Wait-EndpointsHealthy {
    param(
        [string] $HubArmId,
        [int] $TimeoutSeconds,
        [int] $PollSeconds
    )
    # Statuses that mean an endpoint is still throttled/degraded; 'healthy'/'unknown' are treated as recovered.
    $badStatuses = @('dead', 'unhealthy', 'degraded')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ($true) {
        $health = az rest --method get `
            --url "https://management.azure.com$HubArmId/routingEndpointsHealth?api-version=2023-06-30" `
            -o json --only-show-errors | ConvertFrom-Json
        $endpoints = @($health.value)

        foreach ($e in $endpoints | Sort-Object endpointId) {
            $isBad = $badStatuses -contains ("$($e.healthStatus)").ToLower()
            Write-Host ("  {0,-38} {1}" -f $e.endpointId, $e.healthStatus) -ForegroundColor $(if ($isBad) { 'Yellow' } else { 'Green' })
        }

        $bad = @($endpoints | Where-Object { $badStatuses -contains ("$($_.healthStatus)").ToLower() })
        if ($bad.Count -eq 0) {
            return $true
        }
        if ((Get-Date) -ge $deadline) {
            return $false
        }

        Write-Host "  $($bad.Count) endpoint(s) still recovering; re-checking in ${PollSeconds}s..." -ForegroundColor DarkGray
        Start-Sleep -Seconds $PollSeconds
    }
}

# Parse a Service Bus namespace name out of a routing endpoint URI like 'sb://<ns>.servicebus.windows.net'.
function Get-SbNamespace([string] $EndpointUri) {
    return (($EndpointUri -replace '^sb://', '') -split '\.')[0]
}

# Purge a Service Bus queue by delete+recreate (data-plane receive isn't in core az). Test-only:
# this clears ALL messages and restores the queue with its current immutable/config properties.
function Clear-ServiceBusQueue {
    param([string] $Subscription, [string] $ResourceGroup, [string] $Namespace, [string] $Queue)

    $q = az servicebus queue show --subscription $Subscription -g $ResourceGroup --namespace-name $Namespace -n $Queue -o json --only-show-errors | ConvertFrom-Json
    if (-not $q) {
        Write-Host "  queue '$Queue' not found on '$Namespace'; skipping." -ForegroundColor Yellow
        return
    }

    $active = $q.countDetails.activeMessageCount
    Write-Host "  clearing queue '$Namespace/$Queue' ($active active message(s))..." -ForegroundColor Cyan

    az servicebus queue delete --subscription $Subscription -g $ResourceGroup --namespace-name $Namespace -n $Queue --only-show-errors | Out-Null

    $createArgs = @(
        'servicebus', 'queue', 'create', '--subscription', $Subscription, '-g', $ResourceGroup,
        '--namespace-name', $Namespace, '-n', $Queue,
        '--max-size', $q.maxSizeInMegabytes,
        '--lock-duration', $q.lockDuration,
        '--default-message-time-to-live', $q.defaultMessageTimeToLive
    )
    # These properties are immutable at create time, so recreate with the same values.
    if ($q.enablePartitioning) { $createArgs += @('--enable-partitioning', 'true') }
    if ($q.requiresSession) { $createArgs += @('--enable-session', 'true') }
    if ($q.requiresDuplicateDetection) { $createArgs += @('--enable-duplicate-detection', 'true') }

    az @createArgs --only-show-errors | Out-Null
    Write-Host "  queue '$Queue' cleared." -ForegroundColor Green
}

# Drain any subscriptions on a Service Bus topic by delete+recreate (topics without subscriptions hold no backlog).
function Clear-ServiceBusTopic {
    param([string] $Subscription, [string] $ResourceGroup, [string] $Namespace, [string] $Topic)

    $subs = az servicebus topic subscription list --subscription $Subscription -g $ResourceGroup `
        --namespace-name $Namespace --topic-name $Topic -o json --only-show-errors | ConvertFrom-Json
    if (-not $subs -or @($subs).Count -eq 0) {
        Write-Host "  topic '$Namespace/$Topic' has no subscriptions; nothing to clear." -ForegroundColor DarkGray
        return
    }

    foreach ($s in $subs) {
        $active = $s.countDetails.activeMessageCount
        Write-Host "  clearing subscription '$Topic/$($s.name)' ($active active message(s))..." -ForegroundColor Cyan
        az servicebus topic subscription delete --subscription $Subscription -g $ResourceGroup `
            --namespace-name $Namespace --topic-name $Topic -n $s.name --only-show-errors | Out-Null
        $createArgs = @(
            'servicebus', 'topic', 'subscription', 'create', '--subscription', $Subscription, '-g', $ResourceGroup,
            '--namespace-name', $Namespace, '--topic-name', $Topic, '-n', $s.name,
            '--lock-duration', $s.lockDuration,
            '--default-message-time-to-live', $s.defaultMessageTimeToLive
        )
        if ($s.requiresSession) { $createArgs += @('--enable-session', 'true') }
        az @createArgs --only-show-errors | Out-Null
    }
    Write-Host "  topic '$Topic' subscriptions cleared." -ForegroundColor Green
}

# Resolve the hub via ARM (core az; avoids the azure-iot CLI extension).
Write-Host "Resolving IoT Hub '$HubName'..." -ForegroundColor Cyan
$hub = az resource show -g $ResourceGroup -n $HubName --resource-type 'Microsoft.Devices/IotHubs' -o json --only-show-errors | ConvertFrom-Json
if (-not $hub -or [string]::IsNullOrWhiteSpace($hub.properties.hostName)) {
    throw "Could not resolve IoT Hub '$HubName' in resource group '$ResourceGroup'. Are you logged in to the right subscription (az account show)?"
}
$hostname = $hub.properties.hostName

# Reset mode clears queued backlog (test-only) and waits for endpoints to recover naturally - it never scales resources.
if ($Mode -eq 'Reset') {
    Write-Host ""
    Write-Host "Reset mode: clearing pending messages so throttled endpoints can recover naturally." -ForegroundColor Green
    Write-Host ""

    $routing = $hub.properties.routing.endpoints

    # Only Service Bus entities hold a drainable message backlog. Event Hubs (stream/retention),
    # Storage, and Cosmos have nothing to 'clear' - they recover once load stops (cool-down).
    Write-Host "Clearing Service Bus backlog..." -ForegroundColor Cyan
    foreach ($q in @($routing.serviceBusQueues)) {
        if ([string]::IsNullOrWhiteSpace($q.entityPath)) { continue }
        Clear-ServiceBusQueue -Subscription $q.subscriptionId -ResourceGroup $q.resourceGroup `
            -Namespace (Get-SbNamespace $q.endpointUri) -Queue $q.entityPath
    }
    foreach ($t in @($routing.serviceBusTopics)) {
        if ([string]::IsNullOrWhiteSpace($t.entityPath)) { continue }
        Clear-ServiceBusTopic -Subscription $t.subscriptionId -ResourceGroup $t.resourceGroup `
            -Namespace (Get-SbNamespace $t.endpointUri) -Topic $t.entityPath
    }
    Write-Host "Event Hub / Storage / Cosmos have no queued backlog to clear; they recover on cool-down." -ForegroundColor DarkGray

    Write-Host ""
    Write-Host "Waiting for routing endpoints to recover (timeout ${ResetTimeoutSeconds}s, every ${ResetPollSeconds}s)..." -ForegroundColor Cyan
    Write-Host ""
    $recovered = Wait-EndpointsHealthy -HubArmId $hub.id -TimeoutSeconds $ResetTimeoutSeconds -PollSeconds $ResetPollSeconds

    Write-Host ""
    if ($recovered) {
        Write-Host "All routing endpoints recovered - recipients are ready for new messages." -ForegroundColor Green
        exit 0
    }

    Write-Host "Timed out after ${ResetTimeoutSeconds}s; some endpoints are still recovering." -ForegroundColor Yellow
    Write-Host "Ensure no burst is running; Standard-tier throttling clears on its own once load drops." -ForegroundColor DarkGray
    exit 1
}

# Read a shared access policy key via ARM listkeys (no data-plane extension needed).
Write-Host "Reading hub access policy (iothubowner) via ARM..." -ForegroundColor Cyan
$keys = az rest --method post --url "https://management.azure.com$($hub.id)/listkeys?api-version=2023-06-30" -o json --only-show-errors | ConvertFrom-Json
$policy = $keys.value | Where-Object { $_.keyName -eq 'iothubowner' } | Select-Object -First 1
if (-not $policy) { $policy = $keys.value | Where-Object { $_.rights -match 'RegistryWrite' } | Select-Object -First 1 }
if (-not $policy) {
    throw "No shared access policy with RegistryWrite was found on the hub. Ensure local (SAS) auth is enabled on the IoT Hub."
}
$serviceSas = New-HubSasToken -ResourceUri $hostname -Key $policy.primaryKey -PolicyName $policy.keyName -TtlSeconds 3600

# Create the device identity if it does not exist (data-plane registry REST; not created by ARM).
$deviceUri = "https://$hostname/devices/$DeviceId`?api-version=$registryApiVersion"
$registryHeaders = @{ Authorization = $serviceSas; 'Content-Type' = 'application/json' }
$getDevice = Invoke-WebRequest -Uri $deviceUri -Headers $registryHeaders -Method Get -SkipHttpErrorCheck
if ($getDevice.StatusCode -eq 200) {
    $device = $getDevice.Content | ConvertFrom-Json
}
elseif ($getDevice.StatusCode -eq 404) {
    Write-Host "Creating device '$DeviceId'..." -ForegroundColor Cyan
    $createBody = @{ deviceId = $DeviceId; status = 'enabled'; authentication = @{ type = 'sas' } } | ConvertTo-Json
    $putDevice = Invoke-WebRequest -Uri $deviceUri -Headers $registryHeaders -Method Put -Body $createBody -SkipHttpErrorCheck
    if ([int]$putDevice.StatusCode -ge 300) {
        throw "Device create failed ($([int]$putDevice.StatusCode)): $($putDevice.Content)"
    }
    $device = $putDevice.Content | ConvertFrom-Json
}
else {
    throw "Device lookup failed ($([int]$getDevice.StatusCode)): $($getDevice.Content)"
}

$deviceKey = $device.authentication.symmetricKey.primaryKey
if ([string]::IsNullOrWhiteSpace($deviceKey)) {
    throw "Could not read the primary symmetric key for device '$DeviceId'."
}

function New-DeviceSasToken {
    param(
        [string] $Hostname,
        [string] $DeviceId,
        [string] $Key,
        [int] $TtlSeconds = 3600
    )
    $resourceUri = "$Hostname/devices/$DeviceId"
    $encodedUri = [uri]::EscapeDataString($resourceUri)
    $expiry = [System.DateTimeOffset]::UtcNow.AddSeconds($TtlSeconds).ToUnixTimeSeconds()
    $toSign = "$encodedUri`n$expiry"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($Key))
    try {
        $sig = [Convert]::ToBase64String($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($toSign)))
    }
    finally {
        $hmac.Dispose()
    }
    $encodedSig = [uri]::EscapeDataString($sig)
    return "SharedAccessSignature sr=$encodedUri&sig=$encodedSig&se=$expiry"
}

# One SAS token (valid 1 hour) is reused for the whole run.
$sasToken = New-DeviceSasToken -Hostname $hostname -DeviceId $DeviceId -Key $deviceKey -TtlSeconds 3600
$uri = "https://$hostname/devices/$DeviceId/messages/events?api-version=2020-03-13"

# Expand the requested work into a flat list of targets, one entry per message.
$work = foreach ($t in $Targets) {
    for ($i = 0; $i -lt $Count; $i++) { $t }
}
$total = @($work).Count

Write-Host ""
Write-Host "Sending $total message(s) to '$DeviceId' on '$hostname'" -ForegroundColor Green
Write-Host "  Mode     : $Mode" -ForegroundColor Green
Write-Host "  Targets  : $($Targets -join ', ')" -ForegroundColor Green
Write-Host "  Count    : $Count per target" -ForegroundColor Green
Write-Host "  Parallel : $Parallel" -ForegroundColor Green
if ($PayloadBytes -gt 0) { Write-Host "  Payload  : ~$PayloadBytes bytes per message" -ForegroundColor Green }
Write-Host ""

# Precompute the (optionally padded) payload string once and reuse it across runspaces.
$payloadString = if ($PayloadBytes -gt 0) { 'x' * $PayloadBytes } else { 'routing-throttle-test' }

$sw = [System.Diagnostics.Stopwatch]::StartNew()

$results = $work | ForEach-Object -ThrottleLimit $Parallel -Parallel {
    $target = $_
    $headers = @{
        Authorization        = $using:sasToken
        'iothub-app-target'  = $target
        'Content-Type'       = 'application/json'
    }
    $body = @{
        deviceId  = $using:DeviceId
        target    = $target
        timestamp = [System.DateTimeOffset]::UtcNow.ToString('o')
        payload   = $using:payloadString
    } | ConvertTo-Json -Compress

    if ($using:IntervalMs -gt 0) { Start-Sleep -Milliseconds $using:IntervalMs }

    try {
        Invoke-WebRequest -Uri $using:uri -Method Post -Headers $headers -Body $body -SkipHttpErrorCheck | Out-Null
        [pscustomobject]@{ Target = $target; Status = 204; Ok = $true }
    }
    catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        [pscustomobject]@{ Target = $target; Status = $status; Ok = $false }
    }
}

$sw.Stop()

$ok = @($results | Where-Object Ok).Count
$throttled = @($results | Where-Object { $_.Status -eq 429 }).Count
$failed = @($results | Where-Object { -not $_.Ok }).Count

Write-Host ""
Write-Host "Done in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s" -ForegroundColor Green
Write-Host "  Accepted (2xx) : $ok" -ForegroundColor Green
Write-Host "  Throttled (429): $throttled" -ForegroundColor $(if ($throttled -gt 0) { 'Yellow' } else { 'Green' })
Write-Host "  Failed         : $failed" -ForegroundColor $(if ($failed -gt 0) { 'Yellow' } else { 'Green' })
Write-Host ""
Write-Host "Per-target accepted counts:" -ForegroundColor Cyan
$results | Where-Object Ok | Group-Object Target | Sort-Object Name |
    ForEach-Object { Write-Host ("  {0,-10} {1}" -f $_.Name, $_.Count) }

Write-Host ""
Write-Host "Routing throttling is observed downstream (not on the device leg). After sending a" -ForegroundColor DarkGray
Write-Host "large burst, wait 2-5 minutes for metrics to populate, then run:" -ForegroundColor DarkGray
Write-Host "  azmcp iothub routing endpoint-health --hub-name $HubName --resource-group $ResourceGroup" -ForegroundColor DarkGray
