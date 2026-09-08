// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Xml;
using Azure.Core;
using Azure.Mcp.Core.Services.Azure;
using Azure.Mcp.Tools.IoTHub.Commands;
using Azure.Mcp.Tools.IoTHub.Services;
using Azure.ResourceManager;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Services.Azure.Authentication;
using NSubstitute;
using Xunit;

namespace Azure.Mcp.Tools.IoTHub.Tests.Services;

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

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("id1", item.EndpointId);
                Assert.Equal("healthy", item.HealthStatus);
            },
            item =>
            {
                Assert.Equal("id2", item.EndpointId);
                Assert.Equal("unhealthy", item.HealthStatus);
            });
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(nextLink, handler.RequestUris[1]?.ToString());
    }

    [Fact]
    public void ResolveDiagnosticsWindow_DefaultsToPreviousTwentyFourHours()
    {
        var currentTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var result = IoTHubValidation.ResolveDiagnosticsWindow(
            null,
            null,
            null,
            currentTime);

        Assert.Equal(currentTime.AddHours(-24), result.StartTime);
        Assert.Equal(currentTime, result.EndTime);
        Assert.Equal(TimeSpan.FromHours(1), result.Interval);
    }

    [Theory]
    [InlineData(12, "PT1M")]
    [InlineData(24, "PT5M")]
    [InlineData(720, "PT1H")]
    public void ResolveDiagnosticsWindow_AcceptsAtMostSevenHundredTwentyBuckets(
        int durationHours,
        string interval)
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        var result = IoTHubValidation.ResolveDiagnosticsWindow(
            startTime,
            startTime.AddHours(durationHours),
            interval);

        Assert.Equal(XmlConvert.ToTimeSpan(interval), result.Interval);
    }

    [Fact]
    public void ResolveDiagnosticsWindow_RejectsMoreThanSevenHundredTwentyBuckets()
    {
        var currentTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<ArgumentException>(() =>
            IoTHubValidation.ResolveDiagnosticsWindow(null, null, "PT1M", currentTime));

        Assert.Contains("produce 1440 buckets", exception.Message);
        Assert.Contains("maximum is 720", exception.Message);
        Assert.Contains("PT5M or larger", exception.Message);
    }

    [Fact]
    public void BuildMonitorMetricsOptions_AppliesRequestedIntervalAndWindow()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var window = new IoTHubService.ObservationWindow(startTime, startTime.AddHours(2));
        var interval = TimeSpan.FromMinutes(15);

        var options = IoTHubService.BuildMonitorMetricsOptions(
            "Transactions",
            "Microsoft.Storage/storageAccounts",
            window,
            interval,
            "Total",
            "ResponseType eq '*'");

        Assert.Equal("Transactions", options.Metricnames);
        Assert.Equal("Microsoft.Storage/storageAccounts", options.Metricnamespace);
        Assert.Equal(window.Timespan, options.Timespan);
        Assert.Equal(interval, options.Interval);
        Assert.Equal("Total", options.Aggregation);
        Assert.Equal("ResponseType eq '*'", options.Filter);
        Assert.Equal(IoTHubService.MetricSeriesLimit, options.Top);
    }

    [Fact]
    public void BuildMonitorMetricsOptions_OmitsFilterWhenNotProvided()
    {
        var startTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var window = new IoTHubService.ObservationWindow(startTime, startTime.AddHours(6));
        var interval = TimeSpan.FromHours(6);

        var options = IoTHubService.BuildMonitorMetricsOptions(
            "SuccessfulRequests,ServerErrors",
            "Microsoft.EventHub/namespaces",
            window,
            interval,
            "Total");

        Assert.Equal(interval, options.Interval);
        Assert.Null(options.Filter);
        Assert.Null(options.Top);
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
        var responseContent =
            $"{{\"error\":{{\"code\":\"{errorCode}\",\"message\":\"The resource does not exist.\"}}}}";

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
        var azureService = Substitute.For<IAzureService>();
        var logger = Substitute.For<ILogger<IoTHubService>>();

        var cloudConfiguration = Substitute.For<IAzureCloudConfiguration>();
        cloudConfiguration.ArmEnvironment.Returns(ArmEnvironment.AzurePublicCloud);
        azureService.CloudConfiguration.Returns(cloudConfiguration);

        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        azureService.GetTokenCredentialAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(credential));
        azureService.GetClient(Arg.Any<string?>()).Returns(_ => new HttpClient(handler));

        return new IoTHubService(azureService, logger);
    }
}
