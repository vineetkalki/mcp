// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Core.Services.Azure;
using Azure.Mcp.Tools.IoTHub.Models;

namespace Azure.Mcp.Tools.IoTHub.Services;

public interface IIoTHubService
{
    Task<IoTHubDescription> GetIoTHub(
        string hubName,
        string resourceGroup,
        string subscription,
        string? tenant = null,
        CancellationToken cancellationToken = default);

    // Lightweight per-endpoint health verdict (healthy/degraded/unavailable/unreported) from the hub's own
    // routing signals; no target-resource Azure Monitor deep dive.
    Task<List<RoutingEndpointStatus>> GetRoutingEndpointHealth(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default);

    // Per-endpoint routing delivery-latency signals and trend (bucketed by the ISO 8601 interval, e.g. PT1H).
    Task<List<RoutingEndpointLatency>> GetRoutingEndpointLatency(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? interval = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default);

    // Full per-endpoint diagnosis: always runs the target-resource Azure Monitor deep dive and includes the
    // exploration drill-down payload. The latency trend is bucketed by the ISO 8601 interval (e.g. PT1H).
    Task<List<RoutingEndpointDetails>> DiagnoseRoutingEndpoints(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? interval = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default);
}
