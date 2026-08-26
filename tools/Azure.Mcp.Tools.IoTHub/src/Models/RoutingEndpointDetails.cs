// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// One routing endpoint's configuration plus optional computed health. Health is null for config-only projections (iothub get).
public record RoutingEndpointDetails(
    string Name,
    string EndpointType,
    string? EndpointResourceName,
    string? SubscriptionId,
    string? ResourceGroup,
    string? EndpointUri,
    string? EntityPath,
    string? ContainerName,
    string? DatabaseName,
    string? AuthenticationType,
    int? BatchFrequencyInSeconds = null,
    RoutingEndpointHealth? Health = null,
    RoutingTargetResourceSignals? TargetResourceSignals = null,
    RoutingTargetConfigurationSignals? TargetConfigurationSignals = null,
    RoutingEndpointExploration? Exploration = null);
