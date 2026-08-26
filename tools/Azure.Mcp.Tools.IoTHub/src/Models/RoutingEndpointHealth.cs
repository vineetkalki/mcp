// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Computed health for a routing endpoint over the lookback window. ImpactDetails is present only when a deep dive ran.
public record RoutingEndpointHealth(
    string? EndpointHealthStatus = null,
    double? RoutingDeliveryLatencyMsAvg = null,
    double? RoutingDeliveryLatencyMsPeak = null,
    double? LatencyThresholdMs = null,
    double? RoutedDeliverySuccess = null,
    double? RoutedDeliveryFailures = null,
    double? SendToSuccessLatencyMs = null,
    RoutingEndpointImpactDetails? ImpactDetails = null);
