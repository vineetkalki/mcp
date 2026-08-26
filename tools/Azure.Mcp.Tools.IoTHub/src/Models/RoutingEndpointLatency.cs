// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// A routing endpoint's delivery-latency signals over the lookback window.
public record RoutingEndpointLatency(
    string Name,
    string EndpointType,
    string EndpointHealthStatus,
    double? RoutingDeliveryLatencyMsAvg = null,
    double? RoutingDeliveryLatencyMsPeak = null,
    double? LatencyThresholdMs = null,
    double? SendToSuccessLatencyMs = null,
    IReadOnlyList<LatencyTrendPoint>? LatencyTrend = null);
