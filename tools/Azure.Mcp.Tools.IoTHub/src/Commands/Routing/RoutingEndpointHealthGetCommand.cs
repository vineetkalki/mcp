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
    Id = "3f2b9a4e-7c1d-4e8a-9b6f-2a4d8c1e5f73",
    Name = "endpoint-health",
    Title = "Get IoT Hub Routing Endpoint Health",
    Description = """
        Get the health verdict for an IoT Hub's message-routing custom endpoints (Event Hubs, Service Bus
        queues/topics, Blob Storage containers, Cosmos DB SQL containers). Azure Table Storage isn't a
        supported IoT Hub routing destination. Returns endpointHealthStatus per
        endpoint: healthy, degraded, unavailable (the destination cannot be found), or unreported (no
        routing activity in the window). A degraded verdict includes routing failures, IoT Hub endpoint
        failures, or routing latency above the endpoint-aware threshold. This is a fast hub-side check that does not query target-resource
        metrics - use `iothub routing endpoint-latency` for the latency trend or
        `iothub routing endpoint-diagnose` for root-cause analysis. Set --lookback for a relative observation
        window (default 24 hours), or use --start-time with --end-time for an absolute range. Absolute
        ranges take precedence over --lookback. The maximum observation window is 30 days.
        Requires hub-name and resource-group.
        """,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false,
    ReadOnly = true,
    Secret = false,
    LocalRequired = false)]
public sealed class RoutingEndpointHealthGetCommand(
    ILogger<RoutingEndpointHealthGetCommand> logger,
    IIoTHubService service,
    ISubscriptionResolver subscriptionResolver)
    : SubscriptionCommand<RoutingEndpointHealthGetOptions, RoutingEndpointHealthGetCommand.RoutingEndpointHealthGetCommandResult>(subscriptionResolver)
{
    private readonly ILogger<RoutingEndpointHealthGetCommand> _logger = logger;
    private readonly IIoTHubService _service = service;

    public override void ValidateOptions(RoutingEndpointHealthGetOptions options, ValidationResult validationResult)
    {
        base.ValidateOptions(options, validationResult);
        IoTHubValidation.ValidateHubName(options.HubName, validationResult);
        IoTHubValidation.ValidateObservationWindow(
            options.Lookback, options.StartTime, options.EndTime, validationResult);
    }

    public override async Task<CommandResponse> ExecuteAsync(
        CommandContext context,
        RoutingEndpointHealthGetOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = await _service.GetRoutingEndpointHealth(
                options.HubName,
                options.ResourceGroup,
                options.Subscription!,
                options.EndpointName,
                options.Lookback,
                options.StartTime,
                options.EndTime,
                options.Tenant,
                options.RetryPolicy,
                cancellationToken)
                ?? [];

            context.Response.Results = ResponseResult.Create(
                new RoutingEndpointHealthGetCommandResult(endpoints),
                IoTHubJsonContext.Default.RoutingEndpointHealthGetCommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error getting routing endpoint health for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                options.HubName,
                options.ResourceGroup,
                options.Subscription);
            HandleException(context, ex);
        }

        return context.Response;
    }

    public record RoutingEndpointHealthGetCommandResult(
        List<RoutingEndpointStatus> Endpoints);
}
