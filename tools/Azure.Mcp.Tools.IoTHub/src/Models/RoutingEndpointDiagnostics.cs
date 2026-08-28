// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

public sealed record RoutingEndpointDiagnostics(
    RoutingObservationWindow ObservationWindow,
    List<RoutingEndpointDiagnostic> Endpoints);
