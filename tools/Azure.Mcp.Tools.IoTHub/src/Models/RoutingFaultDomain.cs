// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.IoTHub.Models;

// Coarse root-cause attribution for a routing endpoint's degraded health.
public static class RoutingFaultDomain
{
    public const string None = "None";
    public const string IoTHubDelivery = "IoTHubDelivery";
    public const string TargetThrottling = "TargetThrottling";
    public const string TargetServerError = "TargetServerError";
    public const string TargetUserError = "TargetUserError";
    // The hub identity lacks permission to access the target.
    public const string TargetAuthorization = "TargetAuthorization";
    // The target's network configuration may prevent IoT Hub from reaching it.
    public const string TargetNetwork = "TargetNetwork";
    // The target resource does not exist / could not be resolved (deleted, missing, or offline).
    public const string TargetUnavailable = "TargetUnavailable";
    // Evidence too weak to attribute confidently (e.g. a transient latency spike with few failures).
    public const string Inconclusive = "Inconclusive";
    public const string Unknown = "Unknown";
}
