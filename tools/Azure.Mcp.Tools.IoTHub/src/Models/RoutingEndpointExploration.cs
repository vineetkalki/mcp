// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Drill-down aids for a routing endpoint: the resolved target resource id, the metric names used to
// assess it, and ready-to-run `azmcp` commands chosen for the likely fault domain (e.g. RBAC or
// resource-existence checks, or a target metric trend) to investigate the failure further.
public record RoutingEndpointExploration(
    string? TargetResourceId = null,
    IReadOnlyList<string>? MetricsQueried = null,
    IReadOnlyList<string>? DrillDownCommands = null);
