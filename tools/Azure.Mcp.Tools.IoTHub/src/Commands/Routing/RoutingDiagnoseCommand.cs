// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Core.Commands.Subscription;
using Azure.Mcp.Core.Services.Azure.Subscription;
using Azure.Mcp.Tools.IoTHub.Models;
using Azure.Mcp.Tools.IoTHub.Options.Routing;
using Azure.Mcp.Tools.IoTHub.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Models.Command;

namespace Azure.Mcp.Tools.IoTHub.Commands.Routing;

[CommandMetadata(
    Id = "1a6e8c34-5d92-4b70-9f28-3e7a1c4d6b85",
    Name = "endpoint-diagnose",
    Title = "Diagnose IoT Hub Routing Endpoints",
    Description = """
        Diagnose the root cause of message-routing problems for an IoT Hub's custom endpoints (Event Hubs,
        Service Bus queues/topics, Blob Storage containers, Cosmos DB SQL containers). Azure Table Storage
        isn't a supported IoT Hub routing destination. Always runs a
        per-target-resource Azure Monitor deep dive and inspects supported target configuration such as
        Storage firewall settings. Per endpoint returns endpointHealthStatus, latency
        signals, routing-event counts, configuration evidence, and impactDetails (error/throughput breakdown,
        latencyTrend, likelyFaultDomain, confidenceScore, and a user-friendly likelyFaultDetail narrative), plus an exploration payload
        with the resolved target resource id and fault-aware `azmcp` drill-down commands chosen for the
        likely fault domain (RBAC or resource-existence checks when the target is unavailable or returns
        authorization errors, otherwise a target or hub metric trend). Throttling is favored as the root
        cause over the routing failures it produces; an unresolvable target is reported unavailable
        (likelyFaultDomain TargetUnavailable) with drill-downs that confirm whether it still exists and is
        accessible. Pass --endpoint-name for a single endpoint. Use --lookback for a relative window
        (default 24 hours), or --start-time with --end-time for an absolute range of up to 30 days; the
        absolute range takes precedence. --interval controls the latency-trend bucket size.
        Requires hub-name and resource-group.
        """,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false,
    ReadOnly = true,
    Secret = false,
    LocalRequired = false)]
public sealed class RoutingDiagnoseCommand(
    ILogger<RoutingDiagnoseCommand> logger,
    IIoTHubService service,
    ISubscriptionResolver subscriptionResolver)
    : SubscriptionCommand<RoutingDiagnoseOptions, RoutingDiagnoseCommand.RoutingDiagnoseCommandResult>(subscriptionResolver)
{
    private readonly ILogger<RoutingDiagnoseCommand> _logger = logger;
    private readonly IIoTHubService _service = service;

    public override void ValidateOptions(RoutingDiagnoseOptions options, ValidationResult validationResult)
    {
        base.ValidateOptions(options, validationResult);
        IoTHubValidation.ValidateHubName(options.HubName, validationResult);
        IoTHubValidation.ValidateObservationWindow(
            options.Lookback, options.StartTime, options.EndTime, validationResult);
        IoTHubValidation.ValidateInterval(options.Interval, validationResult);
    }

    public override async Task<CommandResponse> ExecuteAsync(
        CommandContext context,
        RoutingDiagnoseOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = await _service.DiagnoseRoutingEndpoints(
                options.HubName,
                options.ResourceGroup,
                options.Subscription!,
                options.EndpointName,
                options.Lookback,
                options.StartTime,
                options.EndTime,
                options.Interval,
                options.Tenant,
                options.RetryPolicy,
                cancellationToken)
                ?? [];

            context.Response.Results = ResponseResult.Create(
                new RoutingDiagnoseCommandResult(endpoints),
                IoTHubJsonContext.Default.RoutingDiagnoseCommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error diagnosing routing endpoints for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                options.HubName,
                options.ResourceGroup,
                options.Subscription);
            HandleException(context, ex);
        }

        return context.Response;
    }

    public record RoutingDiagnoseCommandResult(
        List<RoutingEndpointDetails> Endpoints);
}
