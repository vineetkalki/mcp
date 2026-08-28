// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

public sealed record RoutingTargetInfo(
    string ResolutionStatus,
    string? ResourceId = null,
    string? ResourceType = null,
    string? RoutedResourceId = null,
    string? ExistenceStatus = null,
    string? ExistenceObservedAt = null);
