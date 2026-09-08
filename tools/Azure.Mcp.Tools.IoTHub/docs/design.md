# IoT Hub Routing Endpoint Health and Diagnostics

**Scope:** Azure MCP IoT Hub toolset

## Summary

Expose two read-only MCP tools:

1. `iothub routing endpoint-health`
2. `iothub routing endpoint-diagnostics`

`endpoint-health` is a point-in-time adapter over the IoT Hub
[`routingEndpointsHealth`](https://learn.microsoft.com/rest/api/iothub/iot-hub-resource/get-endpoint-health?view=rest-iothub-2023-06-30)
REST operation. It returns the service-reported endpoint-health fields for each configured custom
endpoint.

`endpoint-diagnostics` gathers time-windowed evidence from IoT Hub and every resolvable, supported
downstream resource. The agent receives the facts needed to explain whether the evidence points toward
the IoT Hub routing path, the downstream resource, both, or neither.

## User stories

| User intent | Example user query | MCP tool selected by the agent |
| --- | --- | --- |
| Check all endpoints now | "Are the destinations for my device messages accepting messages right now?" | `iothub routing endpoint-health` |
| Check one endpoint now | "What is IoT Hub reporting for the `orders-queue` destination?" | `iothub routing endpoint-health --endpoint-name orders-queue` |
| Investigate a recent issue | "Messages were delayed this morning. Show me what IoT Hub and the destination reported." | `iothub routing endpoint-diagnostics --start-time <start> --end-time <end>` |
| Investigate one endpoint | "Why were messages to `archive-storage` failing today?" | `iothub routing endpoint-diagnostics --endpoint-name archive-storage` |

The agent resolves subscription, resource group, hub name, endpoint name, and timestamps from
conversation context or asks for missing required values.

## Goals

- Keep endpoint health faithful to the IoT Hub REST response.
- Keep snapshot evidence separate from time-windowed evidence.
- Return native metric names, aggregations, dimensions, and values.
- Query native downstream metrics for every resolvable, supported endpoint.
- Check current target existence for every resolvable endpoint.
- Surface missing permissions, unresolved targets, unsupported metrics, and partial data explicitly.
- Return evidence only, leaving fault attribution and root-cause explanation to the agent.
- Remain stateless, thread-safe, AOT-safe, transport-independent, and read-only.

## Non-goals


- Convert current endpoint state into historical endpoint health.
- Define an Azure latency SLA or classify a latency value as excessive.
- Repair routing configuration, role assignments, networking, or downstream resources.
- Query the downstream data plane or use endpoint connection strings.
- Attribute parent-resource metrics to one queue, topic, event hub, container, or database container.
- Determine overall hub health or distinguish absent device ingress from route-selection problems.

## API

### 1. Endpoint health

Returns the current endpoint-health snapshot reported by IoT Hub.

```console
azmcp iothub routing endpoint-health \
  --subscription <subscription> \
  --resource-group <resource-group> \
  --hub-name <hub-name> \
  [--endpoint-name <endpoint>]
```

Inputs:

| Option | Required | Meaning |
| --- | --- | --- |
| `--subscription` | No | Subscription ID or name. Uses the normal subscription resolution behavior. |
| `--resource-group` | Yes | Resource group containing the IoT Hub. |
| `--hub-name` | Yes | IoT Hub name. |
| `--endpoint-name` | No | Return one configured custom endpoint. If omitted, return all records. |
| `--tenant` | No | Tenant used for Azure authentication. |
| `--retry-*` | No | Standard retry-policy options. |

The backing REST operation returns a current snapshot, so this tool takes no time-range options.

#### Endpoint health result

```jsonc
{
  "value": [
    {
      "endpointId": "id1",
      "endpointName": "orders-eventhub",
      "healthStatus": "healthy",
      "lastKnownError": null,
      "lastKnownErrorTime": null,
      "lastSuccessfulSendAttemptTime": "Wed, 27 Aug 2026 20:00:00 GMT",
      "lastSendAttemptTime": "Wed, 27 Aug 2026 20:00:00 GMT"
    }
  ]
}
```

The MCP tool passes through the REST fields:

- `endpointId`
- `healthStatus`
- `lastKnownError`
- `lastKnownErrorTime`
- `lastSuccessfulSendAttemptTime`
- `lastSendAttemptTime`

`endpointName` is the only MCP-added field. The REST API identifies endpoints by generated endpoint ID,
so the tool reads the IoT Hub routing configuration to map that ID to the customer-defined endpoint name
and to support `--endpoint-name` filtering.

The tool preserves the service-provided `healthStatus`. Documented values include `unknown`, `healthy`,
`degraded`, `unhealthy`, and `dead`. Other service values, including `forbidden` when returned, are
passed through unchanged.

The MCP response consumes all REST pages and therefore does not return `nextLink`.

If `--endpoint-name` does not match a configured custom endpoint, the command returns a not-found error
that names the requested endpoint and IoT Hub. It does not return an empty successful result.

### 2. Endpoint diagnostics

Returns time-windowed hub and endpoint evidence. It queries native endpoint metrics for every resolvable,
supported endpoint.

```console
azmcp iothub routing endpoint-diagnostics \
  --subscription <subscription> \
  --resource-group <resource-group> \
  --hub-name <hub-name> \
  [--endpoint-name <endpoint>] \
  [--start-time <datetime> --end-time <datetime>] \
  [--interval <duration>]
```

Inputs:

| Option | Required | Meaning |
| --- | --- | --- |
| `--subscription` | No | Subscription ID or name. |
| `--resource-group` | Yes | Resource group containing the IoT Hub. |
| `--hub-name` | Yes | IoT Hub name. |
| `--endpoint-name` | No | Diagnose one configured endpoint. If omitted, diagnose all endpoints. |
| `--start-time` | Paired | Inclusive UTC-normalized start time. Must be supplied with `--end-time`. |
| `--end-time` | Paired | Inclusive UTC-normalized end time. Must be supplied with `--start-time`. |
| `--interval` | No | Azure Monitor bucket size. Defaults to `PT1H`. |
| `--tenant` | No | Tenant used for Azure authentication. |
| `--retry-*` | No | Standard retry-policy options. |

If neither timestamp is supplied, the effective window is the 24 hours ending when the command begins.
If either timestamp is supplied, both are required. The maximum window remains 30 days. Supported
intervals are `PT1M`, `PT5M`, `PT15M`, `PT30M`, `PT1H`, `PT6H`, `PT12H`, and `P1D`.

The selected window and interval must produce no more than 720 buckets:

```text
ceiling((end-time - start-time) / interval) <= 720
```

Invalid combinations are rejected with a message that reports the requested bucket count and the
minimum supported interval for that window. This permits 30 days at `PT1H`, 24 hours at `PT5M`, and 12
hours at `PT1M`.

The effective window is echoed in the result so an agent never has to infer which default was used.

## Endpoint diagnostics result

```jsonc
{
  "observationWindow": {
    "startTime": "2026-08-26T20:00:00Z",
    "endTime": "2026-08-27T20:00:00Z",
    "interval": "PT1H"
  },
  "endpoints": [
    {
      "endpointId": "id1",
      "name": "archive-storage",
      "endpointType": "StorageContainer",
      "authenticationType": "identityBased",
      "endpointUri": "https://account.blob.core.windows.net/",
      "subscriptionId": "00000000-0000-0000-0000-000000000000",
      "resourceGroup": "target-resource-group",
      "endpointResourceName": "account",
      "containerName": "archive",
      "batchFrequencyInSeconds": 300,
      "target": {
        "resolutionStatus": "resolved",
        "resourceId": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/target-resource-group/providers/Microsoft.Storage/storageAccounts/account",
        "resourceType": "Microsoft.Storage/storageAccounts",
        "routedResourceId": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/target-resource-group/providers/Microsoft.Storage/storageAccounts/account/blobServices/default/containers/archive",
        "existenceStatus": "exists",
        "existenceObservedAt": "2026-08-27T20:00:03Z"
      },
      "hubEmitted": {
        "routingMetrics": {
          "metricNamespace": "Microsoft.Devices/IotHubs",
          "metricAvailability": {
            "RoutingDeliveries": "valuesReturned",
            "RoutingDeliveryLatency": "valuesReturned"
          },
          "windowAggregates": {
            "routingDeliveries.result.success.total.count": 50,
            "routingDeliveries.result.failure.failureReasonCategory.endpointUnhealthy.total.count": 10
          },
          "buckets": [
            {
              "timestamp": "2026-08-27T19:00:00Z",
              "routingDeliveries.result.success.total.count": 50,
              "routingDeliveries.result.failure.failureReasonCategory.endpointUnhealthy.total.count": 10,
              "routingDeliveryLatency.avg.ms": 1234.5
            }
          ]
        },
        "errors": []
      },
      "targetEmitted": {
        "queryStatus": "queried",
        "metricScope": "parentResource",
        "metricNamespace": "Microsoft.Storage/storageAccounts",
        "metricAvailability": {
          "Transactions": "valuesReturned"
        },
        "windowAggregates": {
          "transactions.responseType.success.total.count": 50,
          "transactions.responseType.authorizationError.total.count": 10
        },
        "buckets": [
          {
            "timestamp": "2026-08-27T19:00:00Z",
            "transactions.responseType.success.total.count": 50,
            "transactions.responseType.authorizationError.total.count": 10
          }
        ],
        "errors": []
      }
    }
  ]
}
```

Each item in `endpoints` is the self-contained endpoint result. Target resolution/existence,
`hubEmitted`, and `targetEmitted` evidence are nested directly within it. There is no redundant endpoint
wrapper, top-level target map, or reference to resolve.

## Hub-emitted evidence

### Routing metrics

The command makes two Azure Monitor queries over the same observation window and interval:

| Metric | Aggregation | Dimensions |
| --- | --- | --- |
| `RoutingDeliveries` | `Total` | `EndpointName`, `EndpointType`, `Result`, `FailureReasonCategory` |
| `RoutingDeliveryLatency` | `Average` | `EndpointName`, `EndpointType` |

The tool joins both responses by endpoint and bucket timestamp under `hubEmitted.routingMetrics`.
Every metric value is a direct bucket field. Its name keeps the full metric name and encodes dimension
name/value pairs, aggregation, and unit:

```text
{metricName}[.{dimensionName}.{dimensionValue}...].{aggregation}.{unit}
```

Common suffixes are shortened: `Average` becomes `avg`, `Maximum` becomes `max`, `Minimum` becomes `min`,
`Milliseconds` becomes `ms`, and `Percent` becomes `pct`. `Total` and `Count` remain `total` and `count`.
When aggregation and unit are both `Count`, one `count` suffix represents both instead of emitting
`count.count`.

Fields ending in `.count` are JSON integers backed by a signed 64-bit value. Although Azure Monitor
returns metric values through a floating-point API type, the tool verifies that count values are finite,
whole numbers within `Int64` range before conversion. A non-integral or out-of-range count produces a
structured metric error; the tool never rounds it. Fields ending in `.ms` or `.pct` remain JSON numbers
backed by `double`.

Metric and dimension segments retain letters, digits, underscores, and hyphens. Other UTF-8 bytes,
including periods and percent signs, are percent-encoded with uppercase hex before segments are joined.
If two native series still produce the same field name, append `~` plus the first eight lowercase hex
characters of SHA-256 over this canonical identity:

```text
metricNamespace|metricName|aggregation|unit|sorted(dimensionName=dimensionValue)
```

The collision suffix is generated only when needed and is stable for the same native series.

For example, `routingDeliveries.result.failure.failureReasonCategory.endpointUnhealthy.total.count` is
the total count for the `RoutingDeliveries` failure series whose reason is `EndpointUnhealthy`. A field
is omitted when that metric has no value for the bucket.

`windowAggregates` contains only fields that can be aggregated correctly across buckets: successful and
failed delivery totals plus failure-reason totals. It does not contain the latency field because
averaging Azure Monitor bucket averages would be incorrect without each bucket's sample count.

Failure reasons and latency values are reported as native Azure Monitor metrics. `RoutingDeliveryLatency`
covers successful deliveries only.

## Endpoint-side query behavior

Downstream throttling can be transient: IoT Hub can retry and eventually record a successful delivery
even though the downstream service emitted throttling errors during the requested window. Therefore,
the absence of a nonzero `RoutingDeliveries` failure series is not a safe prerequisite for skipping
downstream evidence.

For every configured endpoint, diagnostics:

1. resolves the target resource;
2. queries the native downstream metrics when the target is resolvable and supported;
3. returns the native metric evidence even when hub delivery failures are zero.

This avoids a detection dependency in which downstream throttling could only be discovered after IoT Hub
had already recorded a terminal delivery failure.

For every resolvable endpoint, diagnostics checks the routed resource through ARM. The check runs
regardless of metric activity so an empty metric response can be distinguished from a deleted parent or
routed subresource. Target metric queries and existence checks may run concurrently.

## Target-emitted evidence

For every resolvable, supported endpoint, the command returns the native downstream parent-resource
metrics under that endpoint's `targetEmitted` object. Internally, identical parent-resource queries use
an in-request cache so sibling endpoints do not cause duplicate Azure calls:

| Endpoint type | Resource scope | Native metrics |
| --- | --- | --- |
| Event Hubs | `Microsoft.EventHub/namespaces` | Total `SuccessfulRequests`, `ServerErrors`, `UserErrors`, `ThrottledRequests`, `QuotaExceededErrors` |
| Service Bus queue/topic | `Microsoft.ServiceBus/namespaces` | Total `IncomingMessages`, `IncomingRequests`, `ServerErrors`, `UserErrors`, `ThrottledRequests` |
| Blob Storage container | `Microsoft.Storage/storageAccounts` | Total `Transactions`, split by `ResponseType` |
| Cosmos DB SQL container | `Microsoft.DocumentDB/databaseAccounts` | Count `TotalRequests`, split by `StatusCode`; maximum `NormalizedRUConsumption` |

The output preserves native metric names and dimensions, including Storage response types and Cosmos
status codes.

The requested `--interval` applies to target-emitted metrics as well as hub-emitted metrics. Every native
metric value is placed directly in its timestamp bucket using the same self-describing field-name format.
For example, `transactions.responseType.authorizationError.total.count` identifies the
metric, dimension, aggregation, and unit without requiring a separate lookup.

`metricScope` is always `parentResource`. It makes explicit that the metrics belong to
the namespace or account identified by `target.resourceId`, not exclusively to the routed entity.

For `Total` and `Count`, the window aggregate is the sum of returned bucket values. For `Maximum`, it is
the maximum returned bucket. `Average` fields remain bucket-only unless Azure Monitor returns the sample
count required for a weighted window average. Missing fields are omitted rather than converted to zero.
Count sums use checked `Int64` arithmetic so overflow is surfaced as a structured metric error.

These metrics are usually emitted at the namespace or account scope. They can include activity from
sibling queues, topics, event hubs, containers, databases, or other clients. The response reports that
scope and does not claim that every metric value was caused by the selected routing endpoint. Sibling
endpoints can therefore show the same cached namespace/account values. This duplication is intentional
to keep every endpoint result self-contained; `target.resourceId` makes the shared scope explicit.

## Target resolution and current existence

The endpoint-health REST `endpointId` is an IoT Hub-generated endpoint identifier, not an Azure resource
ID. Building a target resource ID requires subscription, resource group, provider type, and resource
name.

`subscriptionId` and `resourceGroup` are optional IoT Hub endpoint metadata for both key-based and
managed-identity authentication. Endpoint URI parsing can recover an account or namespace name, but
cannot reliably recover subscription and resource group.

The diagnostic result therefore reports one of:

| `resolutionStatus` | Meaning |
| --- | --- |
| `resolved` | One unambiguous target ARM resource ID was constructed. |
| `unresolved` | Required endpoint metadata was absent. No target is guessed. |
| `unsupported` | The endpoint type has no target-query implementation. |

For a resolved target, an ARM existence check describes current state and includes its own observation
time. It is not treated as historical evidence for the requested metric window.

`existenceStatus` is one of:

| Value | Meaning |
| --- | --- |
| `exists` | ARM returned a successful response for the resource or routed subresource. |
| `notFound` | ARM definitively returned a not-found response. |
| `indeterminate` | ARM responded, but authorization or another response prevented a determination. |
| `notChecked` | Resolution failed or the endpoint type does not support an ARM existence check. |

## Query status and errors

Each endpoint's `targetEmitted.queryStatus` is one of:

| Value | Meaning |
| --- | --- |
| `queried` | All applicable target-side metric queries completed. |
| `partial` | Some checks succeeded and others failed or were unsupported. |
| `unresolved` | The target ARM resource ID could not be resolved. |
| `unsupported` | The endpoint type has no target-side metric implementation. |
| `unauthorized` | Required target access was missing. |
| `notFound` | ARM definitively reported that the target metric resource does not exist. |
| `failed` | Target-side metric queries failed for another reported reason. |

Field-presence rules:

- Every endpoint has a `target` object with `resolutionStatus`.
- A resolved target includes `resourceId`, `resourceType`, and `routedResourceId`.
- Every endpoint has `targetEmitted` with `queryStatus`, `windowAggregates`, `buckets`, and `errors`.
- `windowAggregates` and `buckets` are empty for `unresolved`, `unsupported`, `unauthorized`, `notFound`,
  and `failed`, unless a partial query completed before the final status was established.
- A missing metric field is omitted; an explicit zero means Azure Monitor returned zero.
- `metricAvailability` identifies each requested metric independently: `valuesReturned`,
  `noValuesReturned`, `failed`, or `partial`. A successful query with no values is not evidence of
  zero traffic. Numeric values remain in the direct bucket fields, not in this metadata.
- A non-success metric error inside an HTTP 200 response is reported in `errors`, not treated as an
  empty successful query. Successful metrics in the same response are retained.
- Filtered queries explicitly request a series limit. Reaching that limit is reported as possible
  truncation, so incomplete series are not presented as complete evidence.
- Caller cancellation stops collection instead of becoming a resource failure or triggering fallback.
- An existence-check failure is returned as `target.error`; it does not change the independent
  `targetEmitted.queryStatus`. `target.existenceObservedAt` describes a current check, not the historical
  observation window.

Both routing commands use Azure SDK retry defaults and expose no retry-policy options.

Parent-resource metrics may include sibling endpoints and other clients. Even successful target
requests do not prove delivery from this IoT Hub. These tools do not query device ingress, ingress
throttling, or route predicates, so empty routing evidence cannot establish that the hub is idle or
healthy.

Errors are factual and structured:

```jsonc
{
  "source": "targetAzureMonitor",
  "operation": "Microsoft.Insights/metrics/read",
  "resourceId": "/subscriptions/.../providers/Microsoft.ServiceBus/namespaces/example",
  "statusCode": 403,
  "code": "AuthorizationFailed",
  "requiredRole": "Monitoring Reader",
  "message": "Azure Monitor metrics could not be read for the target resource."
}
```

## Permissions

| Evidence | Required access |
| --- | --- |
| IoT Hub configuration | Reader on the IoT Hub or containing scope |
| Endpoint-health tool | `Microsoft.Devices/IotHubs/routingEndpointsHealth/read`, included by Reader |
| IoT Hub routing metrics | `Microsoft.Insights/metrics/read` on the IoT Hub |
| Target existence | Read access on the target resource |
| Target Azure Monitor metrics | `Microsoft.Insights/metrics/read`; Monitoring Reader on the target is the recommended built-in role |

Cross-subscription targets require the caller to have the applicable access in the target subscription.
Missing target permission produces partial diagnostic evidence.

If `--endpoint-name` does not match a configured custom endpoint, diagnostics returns the same not-found
error contract as endpoint health.

Windows longer than 30 days, incomplete timestamp pairs, reversed timestamps, and unsupported intervals
are rejected rather than clamped. A provider-specific Azure Monitor rejection is returned in the
corresponding structured `errors` collection; the tool does not silently change the requested window or
interval.

Azure Monitor aligns buckets to metric time-grain boundaries. `timestamp` is the bucket start, so the
first bucket can begin slightly before the requested start and the final bucket can end slightly after
the requested end. Those boundary buckets contain the values Azure Monitor returned for the requested
timespan; the MCP tool does not relabel or clip them.

The only health value in the two-tool API is the current `healthStatus` returned by the endpoint-health
tool. Endpoint diagnostics contains no health value.

## Validation scenarios

Tests verify:

- endpoint health sends no Azure Monitor or downstream-resource requests;
- endpoint health accepts no time-window options;
- endpoint health preserves every REST field and service status;
- endpoint-name filtering maps configuration name to endpoint ID;
- diagnostics defaults to a 24-hour window and `PT1H` buckets;
- diagnostics does not call `routingEndpointsHealth` or return current endpoint health;
- paired absolute timestamps override the default window;
- window/interval combinations producing more than 720 buckets are rejected;
- native hub metric names, dimensions, aggregations, and buckets are preserved;
- target-side metrics run for every unique resolvable, supported resource, including when hub failures are zero;
- sibling endpoints reuse one cached target query while remaining self-contained in the response;
- every metric field name identifies its metric, dimensions, aggregation, and unit without a lookup;
- every `.count` field is serialized as an integer and invalid count values are rejected rather than rounded;
- transient downstream throttling can be surfaced after an eventually successful hub delivery;
- routed-resource existence is checked even when metrics contain no failures or activity;
- target metrics preserve native names and dimensions;
- target metrics declare `metricScope: parentResource`;
- unsafe field-name segments are escaped and normalized collisions receive a stable hash suffix;
- boundary bucket timestamps can straddle the requested window without being relabeled;
- unresolved key-based and identity-based targets are reported without guessing;
- authorization, not-found, unsupported, and partial-query states are explicit;
- current resource checks are labeled with their observation time.

The integration fixture provisions a Service Bus queue and topic on the same namespace, with
identity-based IoT Hub routes. Integration tests assert both named endpoints exist instead of accepting
empty collections. Historical query timestamps are recording variables so playback uses the original
window. Publishing recordings requires the recording workflow in [recorded tests](../../../docs/recorded-tests.md).

## References

- [IoT Hub Resource - Get Endpoint Health](https://learn.microsoft.com/rest/api/iothub/iot-hub-resource/get-endpoint-health?view=rest-iothub-2023-06-30)
- [Monitoring data reference for Azure IoT Hub](https://learn.microsoft.com/azure/iot-hub/monitor-iot-hub-reference)
- [Azure built-in role definitions](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles)
