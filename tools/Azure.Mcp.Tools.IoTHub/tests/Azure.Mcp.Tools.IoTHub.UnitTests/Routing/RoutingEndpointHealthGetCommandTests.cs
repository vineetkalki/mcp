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

namespace Azure.Mcp.Tools.IoTHub.UnitTests.Routing;

public class RoutingEndpointHealthGetCommandTests : SubscriptionCommandUnitTestsBase<RoutingEndpointHealthGetCommand, IIoTHubService>
{
    [Fact]
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();

        Assert.Equal("endpoint-health", command.Name);
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
            Service.GetRoutingEndpointHealth(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<string?>(),
                Arg.Any<RetryPolicyOptions?>(),
                Arg.Any<CancellationToken>())
                .Returns([new RoutingEndpointStatus("endpoint1", "EventHub", "unreported")]);
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
    [InlineData("hub' OR 1=1")]
    public async Task ExecuteAsync_RejectsInvalidIoTHubName(string invalidName)
    {
        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", invalidName);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("--hub-name must be 3-50 characters long", response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DeserializationValidation()
    {
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([new RoutingEndpointStatus("endpoint1", "EventHub", "healthy")]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingEndpointHealthGetCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("endpoint1", endpoint.Name);
        Assert.Equal("EventHub", endpoint.EndpointType);
        Assert.Equal("healthy", endpoint.EndpointHealthStatus);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("degraded")]
    [InlineData("unavailable")]
    [InlineData("unreported")]
    public async Task ExecuteAsync_SurfacesHealthStatus(string status)
    {
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([new RoutingEndpointStatus("endpoint1", "ServiceBusQueue", status)]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingEndpointHealthGetCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(status, endpoint.EndpointHealthStatus);
    }

    [Fact]
    public async Task ExecuteAsync_PassesEndpointNameToService()
    {
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--endpoint-name", "endpoint1");

        await Service.Received(1).GetRoutingEndpointHealth(
            "hub1",
            "rg1",
            "sub123",
            "endpoint1",
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
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

    [Fact]
    public async Task ExecuteAsync_PassesLookbackToService()
    {
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1", "--lookback", "PT30M");

        await Service.Received(1).GetRoutingEndpointHealth(
            "hub1",
            "rg1",
            "sub123",
            Arg.Any<string?>(),
            "PT30M",
            null,
            null,
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AbsoluteRangeOverridesInvalidLookback()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var endTime = startTime.AddDays(7);
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);

        var response = await ExecuteCommandAsync(
            "--subscription", "sub123",
            "--resource-group", "rg1",
            "--hub-name", "hub1",
            "--lookback", "invalid",
            "--start-time", startTime.ToString("O"),
            "--end-time", endTime.ToString("O"));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        await Service.Received(1).GetRoutingEndpointHealth(
            "hub1",
            "rg1",
            "sub123",
            Arg.Any<string?>(),
            "invalid",
            startTime,
            endTime,
            Arg.Any<string?>(),
            Arg.Any<RetryPolicyOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("--start-time 2026-08-01T00:00:00Z", "--start-time and --end-time must be provided together")]
    [InlineData("--start-time 2026-08-02T00:00:00Z --end-time 2026-08-01T00:00:00Z", "--start-time must be earlier than --end-time")]
    [InlineData("--start-time 2026-07-01T00:00:00Z --end-time 2026-08-01T00:00:01Z", "cannot exceed 30 days")]
    [InlineData("--lookback P31D", "--lookback must be a positive duration no greater than 30 days")]
    public async Task ExecuteAsync_RejectsInvalidObservationWindow(string args, string expectedMessage)
    {
        var response = await ExecuteCommandAsync(
            $"--subscription sub123 --resource-group rg1 --hub-name hub1 {args}");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains(expectedMessage, response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesServiceErrors()
    {
        Service.GetRoutingEndpointHealth(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
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
