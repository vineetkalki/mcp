// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Raw Azure Monitor metrics for the endpoint's target resource (namespace/account/database), surfaced as-is
// for downstream reasoning. These are parent-resource scoped (shared by every sibling endpoint on the same
// resource) and are NOT per-endpoint attributed. MetricsError is set when the metrics could not be read
// (e.g. missing Monitoring Reader role or a blocked network path).
public record RoutingTargetResourceSignals(
    double? SuccessfulRequests = null,
    double? ServerErrors = null,
    double? UserErrors = null,
    double? ThrottledRequests = null,
    IReadOnlyDictionary<string, double>? ErrorBreakdown = null,
    string? MetricsError = null);
