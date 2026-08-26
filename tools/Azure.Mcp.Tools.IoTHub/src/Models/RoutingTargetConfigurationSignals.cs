// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Target configuration evidence used by endpoint-diagnose. These settings do not override observed
// successful deliveries, but can explain failures or identify configuration that warrants review.
public record RoutingTargetConfigurationSignals(
    string? PublicNetworkAccess = null,
    string? NetworkDefaultAction = null,
    string? NetworkBypass = null,
    IReadOnlyList<string>? Warnings = null);
