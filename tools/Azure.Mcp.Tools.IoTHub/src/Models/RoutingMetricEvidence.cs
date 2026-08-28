// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace Azure.Mcp.Tools.IoTHub.Models;

public sealed record RoutingMetricEvidence(
    string MetricNamespace,
    Dictionary<string, JsonElement> WindowAggregates,
    List<Dictionary<string, JsonElement>> Buckets);
