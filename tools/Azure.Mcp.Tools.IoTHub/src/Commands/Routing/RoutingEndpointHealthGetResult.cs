// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Tools.IoTHub.Models;

namespace Azure.Mcp.Tools.IoTHub.Commands.Routing;

public sealed record RoutingEndpointHealthGetResult(
    List<RoutingEndpointHealthSnapshot> Value);
