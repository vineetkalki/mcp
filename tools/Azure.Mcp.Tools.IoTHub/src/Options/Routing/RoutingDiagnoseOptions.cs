// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Core.Options;
using Microsoft.Mcp.Core.Options;

namespace Azure.Mcp.Tools.IoTHub.Options.Routing;

public sealed class RoutingDiagnoseOptions : ISubscriptionOption
{
    [Option(Description = "The name of the IoT Hub.")]
    public required string HubName { get; set; }

    [Option(Description = "The name of a specific routing endpoint to diagnose. If omitted, all configured routing endpoints are diagnosed.")]
    public string? EndpointName { get; set; }

    [Option(Description = "The inclusive observation-window start time in ISO 8601 format. Must be used with --end-time. When both are provided, --lookback is ignored.")]
    public DateTimeOffset? StartTime { get; set; }

    [Option(Description = "The inclusive observation-window end time in ISO 8601 format. Must be used with --start-time. When both are provided, --lookback is ignored.")]
    public DateTimeOffset? EndTime { get; set; }

    [Option(Description = "Size of the observation window as an ISO 8601 duration (e.g. PT2H, PT30M) or a plain number of hours. Defaults to 24 hours.")]
    public string? Lookback { get; set; }

    [Option(Description = "The latency-trend bucket size (Azure Monitor time grain) as an ISO 8601 duration; one of PT1M, PT5M, PT15M, PT30M, PT1H, PT6H, PT12H, or P1D. Defaults to PT1H.")]
    public string? Interval { get; set; }

    [Option(Description = OptionDescriptions.ResourceGroup)]
    public required string ResourceGroup { get; set; }

    [Option(Description = OptionDescriptions.Subscription)]
    public string? Subscription { get; set; }

    [Option(Description = OptionDescriptions.Tenant)]
    public string? Tenant { get; set; }

    [OptionContainer(Prefix = "retry")]
    public RetryPolicyOptions? RetryPolicy { get; set; }
}
