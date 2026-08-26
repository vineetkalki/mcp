// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Fault attribution for a diagnosed endpoint. The raw target-resource counts live on
// RoutingTargetResourceSignals; this record only carries the analysis derived from them.
public record RoutingEndpointImpactDetails(
    IReadOnlyList<LatencyTrendPoint>? LatencyTrend = null,
    double? ConfidenceScore = null,
    string? LikelyFaultDomain = null,
    string? LikelyFaultDetail = null);
