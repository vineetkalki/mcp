// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.Core;
using Azure.Mcp.Core.Services.Azure.Subscription;
using Azure.Mcp.Core.Services.Azure.Tenant;
using Azure.Mcp.Tools.IoTHub.Models;
using Azure.Mcp.Tools.IoTHub.Services;
using Azure.ResourceManager;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Services.Azure.Authentication;
using NSubstitute;
using Xunit;

namespace Azure.Mcp.Tools.IoTHub.UnitTests.Services;

public class IoTHubServiceTests
{
    [Fact]
    public async Task GetRoutingEndpointsHealthAsync_FollowsNextLink()
    {
        const string nextLink = "https://management.azure.com/next-health-page?api-version=2023-06-30";
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"value\":[{{\"endpointId\":\"id1\",\"healthStatus\":\"healthy\"}}],\"nextLink\":\"{nextLink}\"}}")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":[{"endpointId":"id2","healthStatus":"unhealthy"}]}""")
            });
        var service = CreateService(handler);

        var result = await service.GetRoutingEndpointsHealthAsync(
            new ResourceIdentifier(
                "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Devices/IotHubs/hub1"),
            tenant: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("healthy", result["id1"].HealthStatus);
        Assert.Equal("unhealthy", result["id2"].HealthStatus);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(nextLink, handler.RequestUris[1]?.ToString());
    }

    [Fact]
    public void SummarizeLatencyTrend_IgnoresInactiveZeroBuckets()
    {
        var start = new DateTimeOffset(2026, 8, 24, 19, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 6)
            .Select(index => new LatencyTrendPoint(start.AddHours(index), index == 5 ? 111_165 : 0));

        var (average, peak, trend) = IoTHubService.SummarizeLatencyTrend(points);

        Assert.Equal(111_165, average);
        Assert.Equal(111_165, peak);
        var activePoint = Assert.Single(trend);
        Assert.Equal(start.AddHours(5), activePoint.Timestamp);
    }

    [Theory]
    [InlineData("EventHub", null, 300_000)]
    [InlineData("StorageContainer", 60, 300_000)]
    [InlineData("StorageContainer", 600, 1_200_000)]
    public void GetLatencyThresholdMilliseconds_UsesStorageBatchFrequency(
        string endpointType,
        int? batchFrequencyInSeconds,
        double expected)
    {
        var endpoint = CreateEndpoint(endpointType, batchFrequencyInSeconds);

        var threshold = IoTHubService.GetLatencyThresholdMilliseconds(endpoint);

        Assert.Equal(expected, threshold);
    }

    [Theory]
    [InlineData(
        """{"properties":{"publicNetworkAccess":"Enabled","networkAcls":{"defaultAction":"Allow","bypass":"AzureServices"}}}""",
        false)]
    [InlineData(
        """{"properties":{"publicNetworkAccess":"Disabled","networkAcls":{"defaultAction":"Allow","bypass":"AzureServices"}}}""",
        true)]
    [InlineData(
        """{"properties":{"publicNetworkAccess":"Enabled","networkAcls":{"defaultAction":"Deny","bypass":"None"}}}""",
        true)]
    [InlineData(
        """{"properties":{"publicNetworkAccess":"Enabled","networkAcls":{"defaultAction":"Deny","bypass":"Logging, AzureServices"}}}""",
        false)]
    public void ParseStorageConfiguration_IdentifiesNetworkWarnings(
        string content,
        bool expectsWarning)
    {
        var configuration = IoTHubService.ParseStorageConfiguration(content);

        Assert.NotNull(configuration);
        Assert.Equal(expectsWarning, configuration.Warnings?.Count > 0);
    }

    [Fact]
    public void ObservationWindow_MissingTimestamp_IsNotInWindow()
    {
        var endTime = DateTimeOffset.UtcNow;
        var window = new IoTHubService.ObservationWindow(endTime.AddHours(-6), endTime);

        Assert.False(window.Contains(null));
        Assert.False(IoTHubService.HasErrorInWindow("Unauthorized", null, window));
    }

    [Fact]
    public void ObservationWindow_ContainsOnlyTimestampsWithinBounds()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var endTime = startTime.AddHours(6);
        var window = new IoTHubService.ObservationWindow(startTime, endTime);

        Assert.True(window.Contains(startTime));
        Assert.True(window.Contains(endTime));
        Assert.True(IoTHubService.HasErrorInWindow("Unauthorized", startTime.AddHours(1), window));
        Assert.False(window.Contains(startTime.AddTicks(-1)));
        Assert.False(window.Contains(endTime.AddTicks(1)));
    }

    [Fact]
    public void ResolveObservationWindow_AbsoluteRangeOverridesLookback()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(-7));
        var endTime = startTime.AddDays(7);

        var window = IoTHubService.ResolveObservationWindow("invalid", startTime, endTime);

        Assert.Equal(startTime.ToUniversalTime(), window.StartTime);
        Assert.Equal(endTime.ToUniversalTime(), window.EndTime);
        Assert.Equal(TimeSpan.FromDays(7), window.Duration);
    }

    [Fact]
    public void ResolveObservationWindow_RejectsMoreThanThirtyDays()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            IoTHubService.ResolveObservationWindow("P31D", null, null));

        Assert.Contains("cannot exceed 30 days", exception.Message);
    }

    [Fact]
    public void BuildExploration_PreservesRequestedIntervalForTargetAndHubMetrics()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var window = new IoTHubService.ObservationWindow(startTime, startTime.AddHours(2));
        const string subscriptionId = "00000000-0000-0000-0000-000000000001";
        var endpoint = CreateEndpoint("StorageContainer", 60) with
        {
            SubscriptionId = subscriptionId
        };
        var health = new RoutingEndpointHealth(
            EndpointHealthStatus: "degraded",
            ImpactDetails: new RoutingEndpointImpactDetails(
                LikelyFaultDomain: RoutingFaultDomain.TargetNetwork));

        var result = IoTHubService.BuildExploration(
            endpoint,
            new ResourceIdentifier(
                $"/subscriptions/{subscriptionId}/resourceGroups/rg1/providers/Microsoft.Devices/IotHubs/hub1"),
            health,
            window,
            TimeSpan.FromMinutes(15));

        Assert.NotNull(result.DrillDownCommands);
        var metricCommands = result.DrillDownCommands
            .Where(command => command.StartsWith("azmcp monitor metrics query", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, metricCommands.Count);
        Assert.All(metricCommands, command => Assert.Contains("--interval PT15M", command));
        Assert.All(metricCommands, command => Assert.Contains("--start-time 2026-08-01T00:00:00.0000000Z", command));
        Assert.All(metricCommands, command => Assert.Contains("--end-time 2026-08-01T02:00:00.0000000Z", command));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "ResourceNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "ParentResourceNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "EntityNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "MessagingEntityNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "ContainerNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "NotFound")]
    [InlineData(HttpStatusCode.NotFound, "Unrecognized")]
    public void IsResourceNotFoundResponse_RecognizesAzureNotFoundResponses(
        HttpStatusCode statusCode,
        string errorCode)
    {
        var responseContent = $"{{\"error\":{{\"code\":\"{errorCode}\",\"message\":\"The resource does not exist.\"}}}}";

        Assert.True(IoTHubService.IsResourceNotFoundResponse(statusCode, responseContent));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"InvalidRequest"}}""")]
    [InlineData(HttpStatusCode.InternalServerError, "not-json")]
    public void IsResourceNotFoundResponse_RejectsOtherResponses(
        HttpStatusCode statusCode,
        string responseContent)
    {
        Assert.False(IoTHubService.IsResourceNotFoundResponse(statusCode, responseContent));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, null)]
    public async Task ResourceExistsAsync_ReturnsExpectedResult(
        HttpStatusCode statusCode,
        bool? expected)
    {
        var handler = new StubHttpMessageHandler(
            statusCode,
            """{"error":{"code":"AuthorizationFailed"}}""");
        var service = CreateService(handler);
        var resourceId = new ResourceIdentifier(
            "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.ServiceBus/namespaces/ns1/queues/queue1");

        var result = await service.ResourceExistsAsync(
            resourceId,
            "2024-01-01",
            "endpoint1",
            tenant: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal(
            $"https://management.azure.com{resourceId}?api-version=2024-01-01",
            handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("fake-token", handler.AuthorizationParameter);
    }

    private static IoTHubService CreateService(HttpMessageHandler handler)
    {
        var subscriptionService = Substitute.For<ISubscriptionService>();
        var tenantService = Substitute.For<ITenantService>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var logger = Substitute.For<ILogger<IoTHubService>>();

        var cloudConfiguration = Substitute.For<IAzureCloudConfiguration>();
        cloudConfiguration.ArmEnvironment.Returns(ArmEnvironment.AzurePublicCloud);
        tenantService.CloudConfiguration.Returns(cloudConfiguration);

        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        tenantService.GetTokenCredentialAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(credential));

        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler));

        return new IoTHubService(subscriptionService, tenantService, httpClientFactory, logger);
    }

    private static RoutingEndpointDetails CreateEndpoint(
        string endpointType,
        int? batchFrequencyInSeconds) =>
        new(
            Name: "endpoint1",
            EndpointType: endpointType,
            EndpointResourceName: "resource1",
            SubscriptionId: "sub1",
            ResourceGroup: "rg1",
            EndpointUri: null,
            EntityPath: null,
            ContainerName: null,
            DatabaseName: null,
            AuthenticationType: "identityBased",
            BatchFrequencyInSeconds: batchFrequencyInSeconds);
}
