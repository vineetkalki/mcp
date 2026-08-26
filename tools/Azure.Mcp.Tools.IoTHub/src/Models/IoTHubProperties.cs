// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

internal sealed class IoTHubProperties
{
    public string? State { get; set; }

    public string? HostName { get; set; }

    public RoutingProperties? Routing { get; set; }
}
