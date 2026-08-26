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
    Id = "b7d4c1a2-9e63-4f57-8a10-6c2f5b0d9e41",
    Name = "endpoint-latency",
    Title = "Get IoT Hub Routing Endpoint Latency",
    Description = """
        Get message-routing delivery-latency for an IoT Hub's custom endpoints (Event Hubs, Service Bus
        queues/topics, Blob Storage containers, Cosmos DB SQL containers). Azure Table Storage isn't a
        supported IoT Hub routing destination. Per endpoint returns
        endpointHealthStatus, active-bucket average and peak Azure Monitor RoutingDeliveryLatency,
        latencyThresholdMs, sendToSuccessLatencyMs (last-send-attempt minus last-successful-send lag), and
        latencyTrend (active hourly averages). Latency at or above the endpoint-aware threshold marks the
        endpoint as degraded. When the destination is unavailable the latency signals are omitted and only the
        status is returned. Pass --endpoint-name for a single endpoint. Use --lookback for a relative window
        (default 24 hours), or --start-time with --end-time for an absolute range of up to 30 days; the
        absolute range takes precedence. --interval controls the trend bucket size.
        Requires hub-name and resource-group.
        """,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false,
    ReadOnly = true,
    Secret = false,
    LocalRequired = false)]
public sealed class RoutingLatencyGetCommand(
    ILogger<RoutingLatencyGetCommand> logger,
    IIoTHubService service,
    ISubscriptionResolver subscriptionResolver)
    : SubscriptionCommand<RoutingLatencyGetOptions, RoutingLatencyGetCommand.RoutingLatencyGetCommandResult>(subscriptionResolver)
{
    private readonly ILogger<RoutingLatencyGetCommand> _logger = logger;
    private readonly IIoTHubService _service = service;

    public override void ValidateOptions(RoutingLatencyGetOptions options, ValidationResult validationResult)
    {
        base.ValidateOptions(options, validationResult);
        IoTHubValidation.ValidateHubName(options.HubName, validationResult);
        IoTHubValidation.ValidateObservationWindow(
            options.Lookback, options.StartTime, options.EndTime, validationResult);
        IoTHubValidation.ValidateInterval(options.Interval, validationResult);
    }

    public override async Task<CommandResponse> ExecuteAsync(
        CommandContext context,
        RoutingLatencyGetOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = await _service.GetRoutingEndpointLatency(
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
                new RoutingLatencyGetCommandResult(endpoints),
                IoTHubJsonContext.Default.RoutingLatencyGetCommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error getting routing endpoint latency for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                options.HubName,
                options.ResourceGroup,
                options.Subscription);
            HandleException(context, ex);
        }

        return context.Response;
    }

    public record RoutingLatencyGetCommandResult(
        List<RoutingEndpointLatency> Endpoints);
}
