// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// One hourly point in a routing endpoint's delivery-latency trend (average RoutingDeliveryLatency in ms).
public record LatencyTrendPoint(
    DateTimeOffset Timestamp,
    double LatencyMsAvg);
