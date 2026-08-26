// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// A routing endpoint's type and computed health verdict.
public record RoutingEndpointStatus(
    string Name,
    string EndpointType,
    string EndpointHealthStatus);
