# IoT Hub Routing Endpoint Diagnostics

**Status:** Proposal | **Scope:** Azure MCP IoT Hub toolset

## Summary

Add three read-only Azure MCP commands that help users assess IoT Hub routing endpoint availability,
latency, and likely root cause:

- `iothub routing endpoint-health`
- `iothub routing endpoint-latency`
- `iothub routing endpoint-diagnose`

The commands correlate IoT Hub configuration, endpoint-health records, Azure Monitor routing metrics,
target-resource metrics, target existence, and selected target configuration. This provides a useful
answer even when any individual Azure signal is delayed or incomplete.

## Decision requested

Approve the three-command API, health semantics, 30-day observation-window limit, and correlated
destination deep-dive approach described below.

## User story

As a person responsible for an IoT solution, I want to ask natural-language questions about where my
device messages are going, whether those destinations are working, and what I should fix. I should not
need to know Azure Monitor metric names, endpoint types, or MCP command syntax.

### Example user queries and command selection

| User intent | Example user query | MCP command selected by the agent |
| --- | --- | --- |
| Check everything now | "Are all the places my device messages go working?" | `iothub routing endpoint-health` |
| Check a specific historical period | "How were my message destinations doing yesterday between 9 AM and noon?" | `iothub routing endpoint-health --start-time <start> --end-time <end>` |
| Investigate slow delivery | "Why are my device messages taking so long to arrive?" | `iothub routing endpoint-latency` |
| View a latency trend | "Show whether message delivery got slower over the last day." | `iothub routing endpoint-latency --lookback PT24H --interval PT1H` |
| Diagnose a general failure | "Some messages are not arriving. Can you tell me why?" | `iothub routing endpoint-diagnose` |
| Diagnose one destination | "Why is `storage-noauth-endpoint` failing?" | `iothub routing endpoint-diagnose --endpoint-name storage-noauth-endpoint` |

The agent resolves the subscription, resource group, IoT Hub, endpoint name, time range, and interval from
conversation context or asks for any required value that is missing. Users do not need to provide CLI
flags.

The answer should:

- cover all configured custom endpoints or one selected endpoint;
- prioritize delivery availability and latency;
- distinguish missing resources, permissions, networking, throttling, and target errors;
- support relative and absolute time windows;
- provide evidence and follow-up commands without exposing secrets.

## Goals

- Return a consistent per-endpoint health verdict.
- Detect deleted parent resources and routed subresources.
- Surface completed-delivery latency and send-to-success lag.
- Correlate endpoint-level failures with destination-level metrics.
- Provide actionable fault attribution and evidence.
- Work in local and remote MCP transports.
- Remain stateless, thread-safe, AOT-safe, and read-only.

## Non-goals

- Repair endpoint configuration or Azure resources.
- Replace Azure Monitor alerts or diagnostic logs.
- Guarantee immediate status updates; Azure health and metrics are eventually consistent.
- Support unsupported destinations such as Azure Table Storage. IoT Hub Storage routing supports Blob
  Storage and ADLS Gen2 containers.
- Attribute every parent-resource error to a specific endpoint without endpoint-level failure evidence.

## Proposal

Provide a progressive troubleshooting workflow:

1. start with a low-cost health summary;
2. inspect latency and trend data when performance matters;
3. run a destination deep dive when root-cause evidence is required.

All commands share one endpoint projection and observation-window model so their results remain
consistent.

## API

### 1. Endpoint health

Use for a fast summary across all custom routing endpoints.

```console
azmcp iothub routing endpoint-health \
  --subscription <subscription> \
  --resource-group <resource-group> \
  --hub-name <hub-name> \
  [--endpoint-name <endpoint>] \
  [--lookback <duration>] \
  [--start-time <datetime> --end-time <datetime>]
```

### 2. Endpoint latency

Adds average and peak active-bucket latency, threshold, send-to-success lag, and a time-bucket trend.

```console
azmcp iothub routing endpoint-latency \
  --subscription <subscription> \
  --resource-group <resource-group> \
  --hub-name <hub-name> \
  [--endpoint-name <endpoint>] \
  [--lookback <duration>] \
  [--start-time <datetime> --end-time <datetime>] \
  [--interval <duration>]
```

### 3. Endpoint diagnosis

Adds destination metrics, supported configuration checks, fault attribution, confidence, evidence text,
and copy/paste exploration commands.

```console
azmcp iothub routing endpoint-diagnose \
  --subscription <subscription> \
  --resource-group <resource-group> \
  --hub-name <hub-name> \
  [--endpoint-name <endpoint>] \
  [--lookback <duration>] \
  [--start-time <datetime> --end-time <datetime>] \
  [--interval <duration>]
```

### Response contracts

All three commands use the same standard response envelope:

```jsonc
{
  "status": 200,
  "message": "Success",
  "results": {
    // Command-specific result shape
  },
  "duration": 1234
}
```

Only the `results` shape changes with the diagnostic depth:

| Command | Result contents |
| --- | --- |
| `endpoint-health` | Endpoint identity and health verdict. |
| `endpoint-latency` | Health fields plus latency measurements and trend data. |
| `endpoint-diagnose` | Endpoint configuration, nested health details, target signals, and exploration commands. |

The examples below show only the command-specific `results` value. Properties whose values are `null`
are omitted.

#### Endpoint health results

```jsonc
{
  "endpoints": [
    {
      "name": "endpoint-name",
      "endpointType": "EventHub",
      "endpointHealthStatus": "healthy"
    }
  ]
}
```

`endpointType` values:

- `EventHub`
- `ServiceBusQueue`
- `ServiceBusTopic`
- `StorageContainer`
- `CosmosDBSqlContainer`

`endpointHealthStatus` uses the verdicts defined under [Health semantics](#health-semantics).

#### Endpoint latency results

```jsonc
{
  "endpoints": [
    {
      "name": "endpoint-name",
      "endpointType": "EventHub",
      "endpointHealthStatus": "healthy",
      "routingDeliveryLatencyMsAvg": 123.4,
      "routingDeliveryLatencyMsPeak": 150.0,
      "latencyThresholdMs": 300000,
      "sendToSuccessLatencyMs": 0,
      "latencyTrend": [
        {
          "timestamp": "2026-08-25T06:59:00+00:00",
          "latencyMsAvg": 123.4
        }
      ]
    }
  ]
}
```

Latency measurements and the trend are omitted when no delivery-latency sample is available. They are
also omitted when the endpoint is `unavailable`.

#### Endpoint diagnosis results

```jsonc
{
  "endpoints": [
    {
      "name": "endpoint-name",
      "endpointType": "StorageContainer",
      "endpointResourceName": "storage-account",
      "subscriptionId": "00000000-0000-0000-0000-000000000000",
      "resourceGroup": "resource-group",
      "endpointUri": "https://storage-account.blob.core.windows.net/",
      "containerName": "container-name",
      "authenticationType": "identityBased",
      "batchFrequencyInSeconds": 60,
      "health": {
        "endpointHealthStatus": "degraded",
        "routingDeliveryLatencyMsAvg": 123.4,
        "routingDeliveryLatencyMsPeak": 150.0,
        "latencyThresholdMs": 300000,
        "routedDeliverySuccess": 50,
        "routedDeliveryFailures": 10,
        "sendToSuccessLatencyMs": 1000,
        "impactDetails": {
          "latencyTrend": [
            {
              "timestamp": "2026-08-25T06:59:00+00:00",
              "latencyMsAvg": 123.4
            }
          ],
          "confidenceScore": 0.9,
          "likelyFaultDomain": "TargetAuthorization",
          "likelyFaultDetail": "User-friendly evidence and explanation."
        }
      },
      "targetResourceSignals": {
        "successfulRequests": 50,
        "serverErrors": 0,
        "userErrors": 10,
        "throttledRequests": 0,
        "errorBreakdown": {
          "AuthorizationError": 10
        },
        "metricsError": "Unable to read one or more target metrics."
      },
      "targetConfigurationSignals": {
        "publicNetworkAccess": "Disabled",
        "networkDefaultAction": "Deny",
        "networkBypass": "None",
        "warnings": [
          "User-friendly configuration warning."
        ]
      },
      "exploration": {
        "targetResourceId": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/resource-group/providers/Microsoft.Storage/storageAccounts/storage-account",
        "metricsQueried": [
          "Transactions",
          "RoutingDeliveries"
        ],
        "drillDownCommands": [
          "azmcp monitor metrics query ..."
        ]
      }
    }
  ]
}
```

`likelyFaultDomain` uses the values defined under [Fault attribution](#fault-attribution).

Endpoint-specific properties:

- Event Hubs, Service Bus queues, and Service Bus topics use `entityPath`.
- Blob Storage endpoints use `containerName` and can include `batchFrequencyInSeconds`.
- Cosmos DB SQL endpoints use `databaseName` and `containerName`.
- `targetConfigurationSignals` is returned for supported configuration checks such as Blob Storage
  network access.
- Target signals, configuration signals, and individual metrics are omitted when unavailable or not
  applicable.

## Time-window behavior

- `--lookback` accepts hours or an ISO 8601 duration and defaults to 24 hours.
- `--start-time` and `--end-time` must be supplied together.
- An absolute range takes precedence over `--lookback`.
- All windows are limited to 30 days.
- Times are normalized to UTC.
- `--interval` controls latency buckets for latency and diagnosis commands and defaults to one hour.
- Supported intervals are `PT1M`, `PT5M`, `PT15M`, `PT30M`, `PT1H`, `PT6H`, `PT12H`, and `P1D`.

## Health semantics

Verdicts use stable default values:

| Verdict | Meaning |
| --- | --- |
| `healthy` | In-window deliveries exist with no endpoint failure or excessive latency. |
| `degraded` | In-window routing failures, IoT Hub failure/error state, or excessive latency exists. |
| `unavailable` | The destination parent resource or routed subresource does not exist. |
| `unreported` | No qualifying in-window delivery or failure evidence exists. |

Precedence:

1. `unavailable`
2. `degraded`
3. `healthy`
4. `unreported`

Target-resource metrics are parent-scoped. They help explain an endpoint failure but do not independently
degrade an endpoint that has successful endpoint-level deliveries.

## Latency semantics

- `routingDeliveryLatencyMsAvg` averages active, nonzero metric buckets.
- `routingDeliveryLatencyMsPeak` is the highest active-bucket average.
- `sendToSuccessLatencyMs` is the lag between the latest send attempt and latest successful attempt.
- The default degradation threshold is five minutes.
- For Blob Storage endpoints, the threshold is at least twice the configured batch frequency, with the
  five-minute floor retained.
- Missing or unavailable endpoints do not report stale latency samples.

## Design

The service evaluates configured endpoints concurrently against shared hub evidence. It then performs
authoritative destination existence checks for all commands and optional destination deep dives for
diagnosis. Evidence is correlated into one result per configured endpoint.

### Architecture and data sources

The visual's nodes represent management-plane calls. The commands do not connect to an Event Hub,
Service Bus entity, Storage container, or Cosmos DB container data plane, and they do not use destination
connection strings. All requests go to the Azure cloud's configured ARM endpoint, so the same flow works
in public and sovereign clouds.

#### IoT Hub configuration

The service first resolves the subscription and constructs the IoT Hub resource ID:

```text
/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/Microsoft.Devices/IotHubs/{hubName}
```

It reads that resource through the Azure Resource Manager SDK's generic-resource client. The response
contains the routing configuration and endpoint definitions used to identify endpoint names, endpoint
IDs, destination resource IDs, entity paths, and container/database names. This is the **IoT Hub -
Configuration** node in the visual.

#### IoT Hub endpoint-health REST API

The Azure Resource Manager SDK does not expose the routing endpoint-health operation used here, so the
service sends an authenticated HTTP GET through `IHttpClientFactory`:

```http
GET {armEndpoint}{iotHubResourceId}/routingEndpointsHealth?api-version=2023-06-30
```

The request uses an ARM access token for the selected tenant. The service follows every `nextLink`
returned by the API. Health records are keyed by routing endpoint ID, while the commands return endpoint
names, so the service joins each health record to the endpoint definitions from the IoT Hub configuration
response. The API contributes the reported health status, last send attempt, last successful send, and
last known error shown by the **IoT Hub - Endpoint health** node.

#### Hub-wide Azure Monitor queries

The service uses `ArmClient.GetMonitorMetricsAsync` against the IoT Hub resource ID. This is the SDK
equivalent of querying the ARM metrics endpoint under
`{iotHubResourceId}/providers/microsoft.insights/metrics`. Two requests run concurrently and query every
endpoint in one pass:

| Metric | Namespace | Aggregation | Dimension filter | Purpose |
| --- | --- | --- | --- | --- |
| `RoutingDeliveries` | `Microsoft.Devices/IotHubs` | `Total` | `EndpointName eq '*' and Result eq '*'` | Sum successful and failed routing events per endpoint. |
| `RoutingDeliveryLatency` | `Microsoft.Devices/IotHubs` | `Average` | `EndpointName eq '*'` | Build average, peak, and interval-based latency trends per endpoint. |

Both queries use the selected observation window and interval. Splitting by `EndpointName` avoids a
separate Azure Monitor request for each configured endpoint. These calls correspond to the **IoT Hub -
Routing deliveries** and **IoT Hub - Delivery latency** nodes.

#### Destination existence through ARM

All three commands verify that each configured destination still exists. The service sends ARM GET
requests to the routed subresource first. If ARM reports it missing, the service checks the parent
resource to distinguish a deleted namespace/account from a deleted queue, topic, event hub, or container.

| Endpoint type | Parent and routed-subresource path | ARM API version |
| --- | --- | --- |
| Event Hubs | `Microsoft.EventHub/namespaces/{namespace}/eventhubs/{eventHub}` | `2024-01-01` |
| Service Bus queue | `Microsoft.ServiceBus/namespaces/{namespace}/queues/{queue}` | `2024-01-01` |
| Service Bus topic | `Microsoft.ServiceBus/namespaces/{namespace}/topics/{topic}` | `2024-01-01` |
| Blob Storage | `Microsoft.Storage/storageAccounts/{account}/blobServices/default/containers/{container}` | `2023-05-01` |
| Cosmos DB SQL | `Microsoft.DocumentDB/databaseAccounts/{account}/sqlDatabases/{database}/containers/{container}` | `2024-05-15` |

Each path is rooted under the destination's configured subscription and resource group. A definitive ARM
not-found response produces `unavailable`; authorization or another inconclusive response does not get
misreported as a missing resource. This is the **Destination - Resource existence** node.

#### Shared evidence used by all commands

| Source | Transport and scope | Request count |
| --- | --- | --- |
| IoT Hub configuration | ARM SDK, IoT Hub resource | Once per command |
| `routingEndpointsHealth` | Direct ARM REST GET, IoT Hub resource, paged | Once per page |
| `RoutingDeliveries` | Azure Monitor through ARM, IoT Hub resource | Once per command |
| `RoutingDeliveryLatency` | Azure Monitor through ARM, IoT Hub resource | Once per command |
| Destination existence | Direct ARM REST GET, routed subresource and sometimes parent | Per endpoint |

#### Additional diagnosis evidence

Only `endpoint-diagnose` performs the destination deep dive. These Azure Monitor calls target the parent
namespace or account rather than the routed entity, use one-hour buckets, and use the selected observation
window:

| Destination | Resource metric namespace and query |
| --- | --- |
| Event Hubs | `Microsoft.EventHub/namespaces`: total `SuccessfulRequests`, `ServerErrors`, `UserErrors`, `ThrottledRequests`, and `QuotaExceededErrors`. |
| Service Bus | `Microsoft.ServiceBus/namespaces`: total `IncomingMessages`, `IncomingRequests`, `ServerErrors`, `UserErrors`, and `ThrottledRequests`. |
| Blob Storage | `Microsoft.Storage/storageAccounts`: total `Transactions`, split with `ResponseType eq '*'`. |
| Cosmos DB | `Microsoft.DocumentDB/databaseAccounts`: count `TotalRequests`, split with `StatusCode eq '*'`, plus maximum `NormalizedRUConsumption`. |

Metrics without dimensions are requested together. If Azure Monitor rejects a batch because one metric is
unsupported, the service retries the metrics individually so the remaining evidence can still be used.
Sibling endpoints that share a namespace or account also share the same in-request deep-dive task.

Diagnosis also reads Blob Storage network configuration (`publicNetworkAccess`, firewall default action,
and bypass) with an ARM GET of the Storage account using API version `2023-05-01`. This is the
**Storage - Network configuration** node and helps distinguish permission failures from network
restrictions.


## Error handling

- Missing destination resources produce `unavailable`.
- Authorization failures reading optional destination metrics are surfaced as partial evidence.
- Per-endpoint evaluation failures are logged and returned as `unreported`; failures loading the hub or
  shared evidence fail the command.
- Invalid windows, intervals, hub names, or endpoint names return validation errors.
- Generated exploration commands preserve the selected absolute range and interval.

## Limitations and risks

- Azure Monitor and endpoint-health records can lag behind current traffic.
- Storage routing is file-batched, so healthy Storage latency is expected to exceed messaging latency.
- Destination metrics shared by sibling entities can contain unrelated errors; endpoint-level routing
  evidence is required before attribution.
- The fast health command performs destination existence checks, so latency scales with endpoint count
  and cross-subscription access.
- Some exact causes still require destination diagnostic logs.
