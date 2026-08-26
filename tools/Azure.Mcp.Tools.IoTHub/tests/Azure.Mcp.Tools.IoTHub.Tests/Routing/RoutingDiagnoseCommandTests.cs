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

public class RoutingDiagnoseCommandTests : SubscriptionCommandUnitTestsBase<RoutingDiagnoseCommand, IIoTHubService>
{
    [Fact]
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();

        Assert.Equal("endpoint-diagnose", command.Name);
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
            Service.DiagnoseRoutingEndpoints(
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
                    new RoutingEndpointDetails(
                        Name: "endpoint1",
                        EndpointType: "EventHub",
                        EndpointResourceName: "eventhub1",
                        SubscriptionId: "sub123",
                        ResourceGroup: "rg-target",
                        EndpointUri: "sb://example.servicebus.windows.net/",
                        EntityPath: "eventhub1",
                        ContainerName: null,
                        DatabaseName: null,
                        AuthenticationType: "keyBased",
                        Health: new(EndpointHealthStatus: "healthy"))
                ]);
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
    [InlineData("hub' OR 1=1")]
    public async Task ExecuteAsync_RejectsInvalidIoTHubName(string invalidName)
    {
        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", invalidName);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("--hub-name must be 3-50 characters long", response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrottledEndpoint_SurfacesDeepDiveDetail()
    {
        Service.DiagnoseRoutingEndpoints(
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
                new RoutingEndpointDetails(
                    Name: "sbqueue-endpoint",
                    EndpointType: "ServiceBusQueue",
                    EndpointResourceName: "sbnamespace1",
                    SubscriptionId: "sub-target",
                    ResourceGroup: "rg-target",
                    EndpointUri: "sb://example.servicebus.windows.net/",
                    EntityPath: "telemetry-queue",
                    ContainerName: null,
                    DatabaseName: null,
                    AuthenticationType: "identityBased",
                    Health: new RoutingEndpointHealth(
                        EndpointHealthStatus: "degraded",
                        RoutingDeliveryLatencyMsAvg: 52.1),
                    TargetResourceSignals: new RoutingTargetResourceSignals(
                        SuccessfulRequests: 89201,
                        ServerErrors: 0,
                        UserErrors: 0,
                        ThrottledRequests: 25))
            ]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingDiagnoseCommandResult);
        var impacted = Assert.Single(result.Endpoints);
        Assert.Equal("sbqueue-endpoint", impacted.Name);
        Assert.Equal("degraded", impacted.Health!.EndpointHealthStatus);
        Assert.NotNull(impacted.TargetResourceSignals);
        Assert.Equal(25, impacted.TargetResourceSignals!.ThrottledRequests);
    }

    [Fact]
    public async Task ExecuteAsync_SurfacesCorrelatedDeepDiveOutput()
    {
        var trend = new List<LatencyTrendPoint>
        {
            new(new DateTimeOffset(2026, 8, 17, 22, 0, 0, TimeSpan.Zero), 40.0),
            new(new DateTimeOffset(2026, 8, 17, 23, 0, 0, TimeSpan.Zero), 61.5),
        };

        Service.DiagnoseRoutingEndpoints(
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
                new RoutingEndpointDetails(
                    Name: "sbqueue-endpoint",
                    EndpointType: "ServiceBusQueue",
                    EndpointResourceName: "sbnamespace1",
                    SubscriptionId: "sub-target",
                    ResourceGroup: "rg-target",
                    EndpointUri: "sb://example.servicebus.windows.net/",
                    EntityPath: "telemetry-queue",
                    ContainerName: null,
                    DatabaseName: null,
                    AuthenticationType: "identityBased",
                    Health: new RoutingEndpointHealth(
                        EndpointHealthStatus: "degraded",
                        RoutingDeliveryLatencyMsAvg: 61.5,
                        ImpactDetails: new RoutingEndpointImpactDetails(
                            LatencyTrend: trend,
                            ConfidenceScore: 0.99,
                            LikelyFaultDomain: RoutingFaultDomain.TargetThrottling)),
                    TargetResourceSignals: new RoutingTargetResourceSignals(
                        ThrottledRequests: 30,
                        UserErrors: 15,
                        ServerErrors: 0),
                    TargetConfigurationSignals: new RoutingTargetConfigurationSignals(
                        PublicNetworkAccess: "Enabled",
                        NetworkDefaultAction: "Deny",
                        NetworkBypass: "AzureServices"),
                    Exploration: new RoutingEndpointExploration(
                        TargetResourceId: "/subscriptions/sub-target/resourceGroups/rg-target/providers/Microsoft.ServiceBus/namespaces/sbnamespace1",
                        MetricsQueried: ["RoutingDeliveries", "RoutingDeliveryLatency", "ThrottledRequests"],
                        DrillDownCommands: ["azmcp monitor metrics query --subscription sub-target --resource sbnamespace1"]))
            ]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingDiagnoseCommandResult);
        var impacted = Assert.Single(result.Endpoints);
        Assert.NotNull(impacted.Health!.ImpactDetails);
        Assert.Equal(RoutingFaultDomain.TargetThrottling, impacted.Health.ImpactDetails!.LikelyFaultDomain);
        Assert.Equal(0.99, impacted.Health.ImpactDetails.ConfidenceScore);
        Assert.NotNull(impacted.Health.ImpactDetails.LatencyTrend);
        Assert.Equal(2, impacted.Health.ImpactDetails.LatencyTrend!.Count);
        Assert.Equal(40.0, impacted.Health.ImpactDetails.LatencyTrend[0].LatencyMsAvg);
        Assert.Equal(61.5, impacted.Health.ImpactDetails.LatencyTrend[1].LatencyMsAvg);
        Assert.NotNull(impacted.TargetConfigurationSignals);
        Assert.Equal("Deny", impacted.TargetConfigurationSignals!.NetworkDefaultAction);

        // Diagnose always includes the exploration drill-down payload.
        Assert.NotNull(impacted.Exploration);
        Assert.Contains("RoutingDeliveries", impacted.Exploration!.MetricsQueried!);
        Assert.NotEmpty(impacted.Exploration.DrillDownCommands!);
    }

    [Fact]
    public async Task ExecuteAsync_SurfacesUnavailableTarget()
    {
        Service.DiagnoseRoutingEndpoints(
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
                new RoutingEndpointDetails(
                    Name: "missing-eh",
                    EndpointType: "EventHub",
                    EndpointResourceName: "deleted-ehns",
                    SubscriptionId: "sub-target",
                    ResourceGroup: "rg-target",
                    EndpointUri: "sb://deleted-ehns.servicebus.windows.net/",
                    EntityPath: "telemetry",
                    ContainerName: null,
                    DatabaseName: null,
                    AuthenticationType: "identityBased",
                    Health: new RoutingEndpointHealth(
                        EndpointHealthStatus: "unavailable",
                        ImpactDetails: new RoutingEndpointImpactDetails(
                            ConfidenceScore: 1.0,
                            LikelyFaultDomain: RoutingFaultDomain.TargetUnavailable,
                            LikelyFaultDetail: "The Event Hubs namespace 'deleted-ehns' was not found.")))
            ]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingDiagnoseCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("unavailable", endpoint.Health!.EndpointHealthStatus);
        Assert.Equal(RoutingFaultDomain.TargetUnavailable, endpoint.Health.ImpactDetails!.LikelyFaultDomain);
        // The exploration drill-down is omitted for unavailable endpoints.
        Assert.Null(endpoint.Exploration);
    }

    [Fact]
    public async Task ExecuteAsync_PassesEndpointNameAndLookbackToService()
    {
        Service.DiagnoseRoutingEndpoints(
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

        await Service.Received(1).DiagnoseRoutingEndpoints(
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
        var endTime = startTime.AddDays(1);
        Service.DiagnoseRoutingEndpoints(
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
            "--interval", "PT30M");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        await Service.Received(1).DiagnoseRoutingEndpoints(
            "hub1",
            "rg1",
            "sub123",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            startTime,
            endTime,
            "PT30M",
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
    public async Task ExecuteAsync_SurfacesPerEndpointAuthorizationError()
    {
        Service.DiagnoseRoutingEndpoints(
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
                new RoutingEndpointDetails(
                    Name: "partner-eh",
                    EndpointType: "EventHub",
                    EndpointResourceName: "partner-ehns",
                    SubscriptionId: "partner-sub",
                    ResourceGroup: "partner-rg",
                    EndpointUri: "sb://partner.servicebus.windows.net/",
                    EntityPath: "telemetry",
                    ContainerName: null,
                    DatabaseName: null,
                    AuthenticationType: "identityBased",
                    Health: new RoutingEndpointHealth(
                        EndpointHealthStatus: "unreported",
                        ImpactDetails: new RoutingEndpointImpactDetails(
                            LikelyFaultDomain: RoutingFaultDomain.Inconclusive,
                            LikelyFaultDetail: "Authorization failed reading Azure Monitor metrics for Microsoft.EventHub/namespaces/partner-ehns.")))
            ]);

        var response = await ExecuteCommandAsync("--subscription", "sub123", "--resource-group", "rg1", "--hub-name", "hub1");

        var result = ValidateAndDeserializeResponse(response, IoTHubJsonContext.Default.RoutingDiagnoseCommandResult);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("partner-eh", endpoint.Name);
        Assert.NotNull(endpoint.Health!.ImpactDetails);
        Assert.Contains("Authorization failed", endpoint.Health.ImpactDetails!.LikelyFaultDetail);
        Assert.Equal(RoutingFaultDomain.Inconclusive, endpoint.Health.ImpactDetails.LikelyFaultDomain);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesServiceErrors()
    {
        Service.DiagnoseRoutingEndpoints(
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

    [Theory]
    // Weak evidence: a few routing failures below the minimum sample => Inconclusive, not a confident guess.
    [InlineData(3, 0, 0, 0, RoutingFaultDomain.Inconclusive)]
    // Parent-level target errors with NO per-endpoint failure => not attributed (shared namespace/account
    // metrics cannot be blamed on a fully-successful endpoint).
    [InlineData(0, 9000, 10, 5, RoutingFaultDomain.None)]
    // With routing failures present, throttle vs server evenly split with no clear majority => Inconclusive.
    [InlineData(100, 500, 500, 0, RoutingFaultDomain.Inconclusive)]
    // A small but unambiguous (100%-one-category) target signal keeps its attribution (low confidence, not
    // collapsed to Inconclusive/IoTHubDelivery) - e.g. a handful of authorization errors.
    [InlineData(50, 0, 0, 9, RoutingFaultDomain.TargetUserError)]
    // Strong, clearly-dominant target throttling alongside routing failures => TargetThrottling.
    [InlineData(100, 9000, 10, 5, RoutingFaultDomain.TargetThrottling)]
    // Strong routing failures with no target errors => IoTHubDelivery.
    [InlineData(9000, 0, 0, 0, RoutingFaultDomain.IoTHubDelivery)]
    // Throttling + user-error rejections behind a large routing-failure count => TargetThrottling (root cause).
    [InlineData(18657, 74, 0, 73, RoutingFaultDomain.TargetThrottling)]
    public void AnalyzeFaultDomain_ClassifiesEvidenceStrength(
        double routedFailures, double throttled, double serverErrors, double userErrors, string expectedDomain)
    {
        var (_, domain) = IoTHubService.AnalyzeFaultDomain(
            iotHubStatus: null,
            sendToSuccessLatency: null,
            latencyThresholdExceeded: false,
            routedFailures: routedFailures,
            serverErrors: serverErrors,
            userErrors: userErrors,
            throttledRequests: throttled);

        Assert.Equal(expectedDomain, domain);
    }

    [Fact]
    public void AnalyzeFaultDomain_HubFailureStatus_AttributesToIoTHubDelivery()
    {
        var (confidence, domain) = IoTHubService.AnalyzeFaultDomain(
            iotHubStatus: "dead",
            sendToSuccessLatency: null,
            latencyThresholdExceeded: false,
            routedFailures: 0,
            serverErrors: 0,
            userErrors: 0,
            throttledRequests: 0);

        Assert.Equal(RoutingFaultDomain.IoTHubDelivery, domain);
        Assert.Equal(0.5, confidence);
    }

    [Fact]
    public void AnalyzeFaultDomain_LatencyBlipWithoutFailures_IsInconclusive()
    {
        var (confidence, domain) = IoTHubService.AnalyzeFaultDomain(
            iotHubStatus: "unknown",
            sendToSuccessLatency: 4000,
            latencyThresholdExceeded: true,
            routedFailures: 0,
            serverErrors: 0,
            userErrors: 0,
            throttledRequests: 0);

        Assert.Equal(RoutingFaultDomain.Inconclusive, domain);
        Assert.Equal(0.3, confidence);
    }

    [Fact]
    public void AnalyzeFaultDomain_AccessDenied_AttributesToTargetAuthorization()
    {
        // A missing RBAC role reports Unauthorized and the target logs authorization errors.
        var (confidence, domain) = IoTHubService.AnalyzeFaultDomain(
            iotHubStatus: "dead",
            sendToSuccessLatency: null,
            latencyThresholdExceeded: false,
            routedFailures: 50,
            serverErrors: 0,
            userErrors: 9,
            throttledRequests: 0,
            targetAccessDenied: true);

        Assert.Equal(RoutingFaultDomain.TargetAuthorization, domain);
        Assert.Equal(0.9, confidence);
    }

    [Fact]
    public void AnalyzeFaultDomain_NetworkRestriction_AttributesToTargetNetwork()
    {
        var (confidence, domain) = IoTHubService.AnalyzeFaultDomain(
            iotHubStatus: "unhealthy",
            sendToSuccessLatency: null,
            latencyThresholdExceeded: false,
            routedFailures: 50,
            serverErrors: 0,
            userErrors: 9,
            throttledRequests: 0,
            targetAccessDenied: true,
            targetNetworkRestricted: true);

        Assert.Equal(RoutingFaultDomain.TargetNetwork, domain);
        Assert.Equal(0.9, confidence);
    }

    [Fact]
    public void CategorizeStorageTransactions_ClassifiesAuthAndClientErrorsAsUser()
    {
        var byResponseType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Success"] = 100,
            ["AuthorizationError"] = 18,          // 403 from a missing RBAC role - a client (4xx) error
            ["AuthenticationError"] = 2,          // 401 - a client (4xx) error
            ["ClientOtherError"] = 5,             // 4xx
            ["SASClientOtherError"] = 3,          // 4xx, does not start with "Client"
            ["AnonymousAuthorizationError"] = 4,  // 403, does not start with "Client"
            ["ClientThrottlingError"] = 7,        // throttling
            ["ServerBusyError"] = 3,              // throttling
            ["ServerOtherError"] = 4,             // 5xx
            ["NetworkError"] = 1                  // treated as server-side
        };

        var (success, user, server, throttled) = IoTHubService.CategorizeStorageTransactions(byResponseType);

        Assert.Equal(100, success);
        Assert.Equal(32, user);       // Authorization + Authentication + ClientOther + SASClientOther + AnonymousAuthorization
        Assert.Equal(5, server);      // ServerOtherError + NetworkError
        Assert.Equal(10, throttled);  // ClientThrottlingError + ServerBusyError
    }
}
