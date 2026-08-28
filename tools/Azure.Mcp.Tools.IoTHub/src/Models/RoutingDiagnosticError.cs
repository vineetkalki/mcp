// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

public sealed record RoutingDiagnosticError(
    string Source,
    string Operation,
    string? ResourceId,
    int? StatusCode,
    string? Code,
    string? RequiredRole,
    string Message);
