// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Mcp.Tests;
using Microsoft.Mcp.Tests.Attributes;
using Microsoft.Mcp.Tests.Client;
using Microsoft.Mcp.Tests.Client.Helpers;
using Xunit;

namespace Azure.Mcp.Tools.IoTHub.Tests;

public class IoTHubCommandTests(
    ITestOutputHelper output,
    TestProxyFixture fixture,
    LiveServerFixture liveServerFixture)
    : RecordedCommandTestsBase(output, fixture, liveServerFixture)
{
    [Fact]
    public async Task IoTHubDevice_ListDevices()
    {
        // ResourceBaseName equals the deployed hub name and is available in both Record and
        // Playback (sanitized to match the recording). The IOTHUB_NAME env var is only set
        // during Record, so relying on it would break playback in CI.
        var hubName = Settings.ResourceBaseName;

        await CallToolAsync("iothub_device_list", new()
        {
            { "hub-name", hubName },
            { "resource-group", Settings.ResourceGroupName },
            { "subscription", Settings.SubscriptionId }
        });

        await CallToolAsync("iothub_device_list", new()
        {
            { "hub-name", hubName },
            { "resource-group", Settings.ResourceGroupName },
            { "subscription", Settings.SubscriptionId },
            { "max-count", 2 }
        });
    }

    [Fact]
    public async Task Should_get_iot_hub_by_name_and_resource_group()
    {
        var result = await CallToolAsync("iothub_hub_get", new()
        {
            { "hub-name", Settings.ResourceBaseName },
            { "resource-group", Settings.ResourceGroupName },
            { "subscription", Settings.SubscriptionId },
            { "tenant", Settings.TenantId }
        });

        Assert.NotNull(result);

        var payload = result!.Value;

        var iotHub = payload.AssertProperty("ioTHub");
        Assert.Equal(JsonValueKind.Object, iotHub.ValueKind);

        var areResultsTruncated =
            payload.AssertProperty("areResultsTruncated");

        Assert.True(
            areResultsTruncated.ValueKind is
            JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    [LiveTestOnly]
    public async Task Should_get_iot_hub_routing_endpoint_health()
    {
        var result = await CallToolAsync("iothub_routing_endpoint-health", new()
        {
            { "hub-name", Settings.ResourceBaseName },
            { "resource-group", Settings.ResourceGroupName },
            { "subscription", Settings.SubscriptionId },
            { "tenant", Settings.TenantId }
        });

        Assert.NotNull(result);
        var value = result.Value.AssertProperty("value");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        foreach (var endpoint in value.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, endpoint.GetProperty("endpointId").ValueKind);
            Assert.Equal(JsonValueKind.String, endpoint.GetProperty("endpointName").ValueKind);
            Assert.Equal(JsonValueKind.String, endpoint.GetProperty("healthStatus").ValueKind);
        }
    }

    [Fact]
    [LiveTestOnly]
    public async Task Should_get_iot_hub_routing_endpoint_diagnostics()
    {
        var result = await CallToolAsync("iothub_routing_endpoint-diagnostics", new()
        {
            { "hub-name", Settings.ResourceBaseName },
            { "resource-group", Settings.ResourceGroupName },
            { "subscription", Settings.SubscriptionId },
            { "tenant", Settings.TenantId }
        });

        Assert.NotNull(result);
        var observationWindow = result.Value.AssertProperty("observationWindow");
        Assert.Equal(JsonValueKind.Object, observationWindow.ValueKind);
        var endpoints = result.Value.AssertProperty("endpoints");
        Assert.Equal(JsonValueKind.Array, endpoints.ValueKind);
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, endpoint.GetProperty("name").ValueKind);
            Assert.Equal(JsonValueKind.String, endpoint.GetProperty("endpointType").ValueKind);
            Assert.Equal(JsonValueKind.Object, endpoint.GetProperty("hubEmitted").ValueKind);
            Assert.Equal(JsonValueKind.Object, endpoint.GetProperty("targetEmitted").ValueKind);
            Assert.False(endpoint.TryGetProperty("health", out _));
        }
    }
}
