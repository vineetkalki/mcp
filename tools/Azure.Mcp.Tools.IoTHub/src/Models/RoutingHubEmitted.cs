// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

public sealed record RoutingHubEmitted(
    RoutingMetricEvidence RoutingMetrics,
    List<RoutingDiagnosticError> Errors);
