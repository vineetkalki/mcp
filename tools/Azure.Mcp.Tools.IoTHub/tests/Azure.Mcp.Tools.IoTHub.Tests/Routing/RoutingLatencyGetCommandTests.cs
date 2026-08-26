// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.Mcp.Tests.Commands;
using Azure.Mcp.Tools.IoTHub.Commands;
using Azure.Mcp.Tools.IoTHub.Commands.Routing;
using Azure.Mcp.Tools.IoTHub.Models;
using Azure.Mcp.Tools.IoTHub.Services;
using Microsoft.Mcp.Core.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Azure.Mcp.Tools.IoTHub.Tests.Routing;

public class RoutingLatencyGetCommandTests : SubscriptionCommandUnitTestsBase<RoutingLatencyGetCommand, IIoTHubService>
{
    [Fact]
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();

        Assert.Equal("endpoint-latency", command.Name);
        Assert.NotNull(command.Description);
        Assert.NotEmpty(command.Description);
    }

    [Theory]
    [InlineData("--subscription sub123 --hub-name hub1", false)]
    [InlineData("--subscription sub123 --resource-group rg1 --hub-name hub1", true)]
    [InlineData("--subscription sub123 --resource-group rg1 --hub-name hub1 --endpoint-name endpoint1", true)]
    [InlineData("--subscription sub123", false)]
    public async Task ExecuteAsync_ValidatesInputCorrectly(string args, bool shouldSucceed)
    {
        if (shouldSucceed)
        {
            Service.GetRoutingEndpointLatency(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<RetryPolicyOptions?>(),
                Arg.Any<CancellationToken>())
                .Returns([new RoutingEndpointLatency("endpoint1", "EventHub", "unreported")]);
        }

        var response = await ExecuteCommandAsync(args);

        Assert.Equal(shouldSucceed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.Status);
        if (!shouldSucceed)
        {
            Assert.Contains("required", response.Message.ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("hub-name-")]
    [InlineData("hub!name")]
    public async Task ExecuteAsync_RejectsInvalidIoTHubName(string invalidName)
    {
        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", invalidName);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("--hub-name must be 3-50 characters long", response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SurfacesLatencyTrend()
    {
        var trend = new List<LatencyTrendPoint>
        {
            new(new DateTimeOffset(2026, 8, 17, 22, 0, 0, TimeSpan.Zero), 40.0),
            new(new DateTimeOffset(2026, 8, 17, 23, 0, 0, TimeSpan.Zero), 61.5),
        };

        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([
                new RoutingEndpointLatency(
                    "eh-endpoint",
                    "EventHub",
                    "healthy",
                    RoutingDeliveryLatencyMsAvg: 50.75,
                    RoutingDeliveryLatencyMsPeak: 61.5,
                    LatencyThresholdMs: 300_000,
                    SendToSuccessLatencyMs: 1200,
                    LatencyTrend: trend)
            ]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingLatencyGetCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("eh-endpoint", endpoint.Name);
        Assert.Equal("healthy", endpoint.EndpointHealthStatus);
        Assert.Equal(50.75, endpoint.RoutingDeliveryLatencyMsAvg);
        Assert.Equal(61.5, endpoint.RoutingDeliveryLatencyMsPeak);
        Assert.Equal(300_000, endpoint.LatencyThresholdMs);
        Assert.Equal(1200, endpoint.SendToSuccessLatencyMs);
        Assert.NotNull(endpoint.LatencyTrend);
        Assert.Equal(2, endpoint.LatencyTrend!.Count);
        Assert.Equal(61.5, endpoint.LatencyTrend[1].LatencyMsAvg);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableTarget_OmitsLatencyHistory()
    {
        // The service suppresses the latency signals when the target resource can't be resolved.
        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([new RoutingEndpointLatency("missing-endpoint", "ServiceBusQueue", "unavailable")]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingLatencyGetCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("missing-endpoint", endpoint.Name);
        Assert.Equal("unavailable", endpoint.EndpointHealthStatus);
        Assert.Null(endpoint.RoutingDeliveryLatencyMsAvg);
        Assert.Null(endpoint.SendToSuccessLatencyMs);
        Assert.Null(endpoint.LatencyTrend);
    }

    [Fact]
    public async Task ExecuteAsync_PassesEndpointNameAndLookbackToService()
    {
        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--endpoint-name", "endpoint1", "--lookback", "PT30M");

        await Service.Received(1).GetRoutingEndpointLatency(
            "hub1",
            "rg1",
            "sub123",
            "endpoint1",
            "PT30M",
            null,
            null,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PassesAbsoluteRangeAndIntervalToService()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var endTime = startTime.AddHours(12);
        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        var response = await ExecuteCommandAsync(
            "--subscription", "sub123",
            "--resource-group", "rg1",
            "--hub-name", "hub1",
            "--start-time", startTime.ToString("O"),
            "--end-time", endTime.ToString("O"),
            "--interval", "PT15M");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        await Service.Received(1).GetRoutingEndpointLatency(
            "hub1",
            "rg1",
            "sub123",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            startTime,
            endTime,
            "PT15M",
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("notaduration")]
    [InlineData("PT0S")]
    public async Task ExecuteAsync_RejectsInvalidLookback(string lookback)
    {
        var response = await ExecuteCommandAsync(
            "--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--lookback", lookback);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("--lookback must be a positive duration no greater than 30 days", response.Message);
    }

    [Theory]
    [InlineData("notaduration")]
    [InlineData("5m")]
    [InlineData("PT0S")]
    [InlineData("PT30S")]
    [InlineData("PT10M")]
    public async Task ExecuteAsync_RejectsInvalidInterval(string interval)
    {
        var response = await ExecuteCommandAsync(
            "--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--interval", interval);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("--interval must be one of the Azure Monitor supported time grains", response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PassesIntervalToService()
    {
        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--interval", "PT5M");

        await Service.Received(1).GetRoutingEndpointLatency(
            "hub1",
            "rg1",
            "sub123",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            "PT5M",
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HandlesServiceErrors()
    {
        Service.GetRoutingEndpointLatency(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Test error"));

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Contains("Test error", response.Message);
        Assert.Contains("troubleshooting", response.Message);
    }
}
