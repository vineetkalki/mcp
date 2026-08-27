// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using Azure.Core;
using Azure.Mcp.Core.Services.Azure;
using Azure.Mcp.Tools.IoTHub.Commands;
using Azure.Mcp.Tools.IoTHub.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.Monitor;
using Azure.ResourceManager.Monitor.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;

namespace Azure.Mcp.Tools.IoTHub.Services;

public class IoTHubService(
    IAzureService azureService,
    ILogger<IoTHubService> logger)
    : BaseAzureService(azureService), IIoTHubService
{
    private readonly ILogger<IoTHubService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IoTHubDescription> GetIoTHub(
        string hubName,
        string resourceGroup,
        string subscription,
        string? tenant = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredParameters(
            (nameof(subscription), subscription),
            (nameof(resourceGroup), resourceGroup),
            (nameof(hubName), hubName));

        try
        {
            var subscriptionResource = await AzureService.GetSubscription(
                subscription,
                tenant,
                cancellationToken: cancellationToken);
            var armClient = await CreateArmClientAsync(
                tenant,
                cancellationToken: cancellationToken);
            var iotHubResourceId = new ResourceIdentifier(
                $"/subscriptions/{subscriptionResource.Data.SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Devices/IotHubs/{hubName}");
            var hub = await armClient.GetGenericResource(iotHubResourceId).GetAsync(cancellationToken);

            return ConvertToIoTHubDescription(hub.Value.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'", hubName, resourceGroup, subscription);
            throw;
        }
    }

    public async Task<List<RoutingEndpointStatus>> GetRoutingEndpointHealth(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateRoutingEndpointsAsync(
            "routing endpoint health",
            hubName, resourceGroup, subscription, endpointName,
            ResolveObservationWindow(lookback, startTime, endTime),
            TimeSpan.FromHours(1), tenant, retryPolicy,
            async (endpoint, ctx, ct) =>
            {
                var (_, status) = await EvaluateEndpointAsync(endpoint, ctx, ct);
                return new RoutingEndpointStatus(endpoint.Name, endpoint.EndpointType, status);
            },
            (endpoint, _) => new RoutingEndpointStatus(endpoint.Name, endpoint.EndpointType, "unreported"),
            cancellationToken);
    }

    public async Task<List<RoutingEndpointLatency>> GetRoutingEndpointLatency(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? interval = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateRoutingEndpointsAsync(
            "routing endpoint latency",
            hubName, resourceGroup, subscription, endpointName,
            ResolveObservationWindow(lookback, startTime, endTime),
            ResolveInterval(interval), tenant, retryPolicy,
            async (endpoint, ctx, ct) =>
            {
                var (signals, status) = await EvaluateEndpointAsync(endpoint, ctx, ct);

                // The target resource is gone: any latency still in the window is stale, so report status only.
                if (string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    return new RoutingEndpointLatency(endpoint.Name, endpoint.EndpointType, status);
                }

                return new RoutingEndpointLatency(
                    endpoint.Name,
                    endpoint.EndpointType,
                    status,
                    RoutingDeliveryLatencyMsAvg: signals.RoutingLatency,
                    RoutingDeliveryLatencyMsPeak: signals.PeakRoutingLatency,
                    LatencyThresholdMs: GetLatencyThresholdMilliseconds(endpoint),
                    SendToSuccessLatencyMs: signals.SendToSuccessLatency,
                    LatencyTrend: signals.LatencyTrend.Count == 0 ? null : signals.LatencyTrend);
            },
            (endpoint, _) => new RoutingEndpointLatency(endpoint.Name, endpoint.EndpointType, "unreported"),
            cancellationToken);
    }

    private async Task<(EndpointBaseSignals Signals, string Status)> EvaluateEndpointAsync(
        RoutingEndpointDetails endpoint,
        EndpointEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var signals = await GetBaseSignalsAsync(
            endpoint,
            context.RoutingDeliveriesTask,
            context.HealthMapTask);
        var status = await ComputeHealthStatusAsync(
            endpoint,
            signals,
            context.Window,
            context.Tenant,
            cancellationToken);
        return (signals, status);
    }

    public async Task<List<RoutingEndpointDetails>> DiagnoseRoutingEndpoints(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? lookback = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? interval = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateRoutingEndpointsAsync(
            "routing endpoint diagnosis",
            hubName, resourceGroup, subscription, endpointName,
            ResolveObservationWindow(lookback, startTime, endTime),
            ResolveInterval(interval), tenant, retryPolicy,
            async (endpoint, ctx, ct) =>
            {
                // Kick the target-resource deep dive off up front so it overlaps the hub-side base signals.
                var targetHealthTask = ctx.GetTargetHealthAsync(
                    endpoint,
                    () => QueryTargetResourceHealthAsync(endpoint, ctx.Window, ctx.Tenant, ctx.RetryPolicy, ct));
                var targetConfigurationTask = ctx.GetTargetConfigurationAsync(
                    endpoint,
                    () => QueryTargetConfigurationAsync(endpoint, ctx.Tenant, ct));
                var signals = await GetBaseSignalsAsync(endpoint, ctx.RoutingDeliveriesTask, ctx.HealthMapTask);
                var (health, targetSignals, targetConfiguration) = await DiagnoseEndpointHealthAsync(
                    endpoint, signals, targetHealthTask, targetConfigurationTask, ctx.Window, ctx.Tenant, ct);
                // Drill-downs are tailored to the likely fault domain so they surface information the result
                // does not already contain (RBAC, resource existence, or a target metric trend).
                return endpoint with
                {
                    Health = health,
                    TargetResourceSignals = targetSignals,
                    TargetConfigurationSignals = targetConfiguration,
                    Exploration = BuildExploration(
                        endpoint, ctx.HubId, health, ctx.Window, ctx.Interval)
                };
            },
            (endpoint, ctx) => endpoint with
            {
                Health = new RoutingEndpointHealth("unreported"),
                Exploration = BuildExploration(
                    endpoint, ctx.HubId, null, ctx.Window, ctx.Interval)
            },
            cancellationToken);
    }

    private sealed class EndpointEvaluationContext(
        ResourceIdentifier hubId,
        Task<IReadOnlyDictionary<string, RoutingDeliverySignals>> routingDeliveriesTask,
        Task<IReadOnlyDictionary<string, EndpointHealthData>> healthMapTask,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<TargetResourceHealth>>> _targetHealth = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<RoutingTargetConfigurationSignals?>>> _targetConfiguration = new(StringComparer.OrdinalIgnoreCase);

        public ResourceIdentifier HubId { get; } = hubId;
        public Task<IReadOnlyDictionary<string, RoutingDeliverySignals>> RoutingDeliveriesTask { get; } = routingDeliveriesTask;
        public Task<IReadOnlyDictionary<string, EndpointHealthData>> HealthMapTask { get; } = healthMapTask;
        public ObservationWindow Window { get; } = window;
        public TimeSpan Interval { get; } = interval;
        public string? Tenant { get; } = tenant;
        public RetryPolicyOptions? RetryPolicy { get; } = retryPolicy;

        public Task<TargetResourceHealth> GetTargetHealthAsync(
            RoutingEndpointDetails endpoint,
            Func<Task<TargetResourceHealth>> factory) =>
            _targetHealth.GetOrAdd(GetTargetCacheKey(endpoint), _ => new(factory)).Value;

        public Task<RoutingTargetConfigurationSignals?> GetTargetConfigurationAsync(
            RoutingEndpointDetails endpoint,
            Func<Task<RoutingTargetConfigurationSignals?>> factory) =>
            _targetConfiguration.GetOrAdd(GetTargetCacheKey(endpoint), _ => new(factory)).Value;
    }

    // Orchestration shared by all three routing commands: validate inputs, load the hub's endpoints and the
    // two hub-wide lookups once, evaluate every endpoint concurrently, and fall back per endpoint on error.
    // Only the per-endpoint projection (evaluate/onError) differs between commands.
    private async Task<List<TResult>> EvaluateRoutingEndpointsAsync<TResult>(
        string operation,
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        Func<RoutingEndpointDetails, EndpointEvaluationContext, CancellationToken, Task<TResult>> evaluate,
        Func<RoutingEndpointDetails, EndpointEvaluationContext, TResult> onError,
        CancellationToken cancellationToken)
    {
        ValidateRequiredParameters(
            (nameof(subscription), subscription),
            (nameof(resourceGroup), resourceGroup),
            (nameof(hubName), hubName));

        try
        {
            var (endpoints, hubId, healthMapTask, routingDeliveriesTask) = await LoadEndpointsAsync(
                hubName, resourceGroup, subscription, endpointName, window, interval, tenant, retryPolicy, cancellationToken);

            var context = new EndpointEvaluationContext(
                hubId, routingDeliveriesTask, healthMapTask, window, interval, tenant, retryPolicy);

            var results = await Task.WhenAll(endpoints.Select(async endpoint =>
            {
                try
                {
                    return await evaluate(endpoint, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to evaluate {Operation} for routing endpoint '{EndpointName}'.", operation, endpoint.Name);
                    return onError(endpoint, context);
                }
            }));

            return [.. results];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving {Operation} for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'", operation, hubName, resourceGroup, subscription);
            throw;
        }
    }

    internal readonly record struct ObservationWindow(DateTimeOffset StartTime, DateTimeOffset EndTime)
    {
        public TimeSpan Duration => EndTime - StartTime;

        public string Timespan => $"{ToIsoString(StartTime)}/{ToIsoString(EndTime)}";

        public bool Contains(DateTimeOffset? timestamp) =>
            timestamp is not null && timestamp >= StartTime && timestamp <= EndTime;
    }

    internal static ObservationWindow ResolveObservationWindow(
        string? lookback,
        DateTimeOffset? startTime,
        DateTimeOffset? endTime)
    {
        if (startTime.HasValue != endTime.HasValue)
        {
            throw new ArgumentException(IoTHubValidation.IncompleteTimeRangeError);
        }

        DateTimeOffset start;
        DateTimeOffset end;
        if (startTime.HasValue && endTime.HasValue)
        {
            start = startTime.Value.ToUniversalTime();
            end = endTime.Value.ToUniversalTime();
        }
        else
        {
            var duration = IoTHubValidation.ParseLookback(lookback);
            end = DateTimeOffset.UtcNow;
            start = end.Subtract(duration);
        }

        if (start >= end)
        {
            throw new ArgumentException(IoTHubValidation.InvalidTimeRangeOrderError);
        }

        if (end - start > IoTHubValidation.MaxObservationWindow)
        {
            throw new ArgumentException(IoTHubValidation.TimeRangeTooLargeError);
        }

        return new(start, end);
    }

    // The trend bucket (Azure Monitor time grain) as an ISO 8601 duration (one of PT1M, PT5M, PT15M, PT30M, PT1H, PT6H, PT12H, P1D); default PT1H.
    private static TimeSpan ResolveInterval(string? interval) =>
        string.IsNullOrWhiteSpace(interval) ? TimeSpan.FromHours(1) : XmlConvert.ToTimeSpan(interval);

    // Loads the hub, projects its routing endpoints, filters to a single endpoint when requested, and
    // starts the two hub-wide lookups concurrently (routingEndpointsHealth and the RoutingDeliveries /
    // RoutingDeliveryLatency metrics for all endpoints), so they overlap the per-endpoint deep dives.
    private async Task<(List<RoutingEndpointDetails> Endpoints, ResourceIdentifier HubId, Task<IReadOnlyDictionary<string, EndpointHealthData>> HealthMapTask, Task<IReadOnlyDictionary<string, RoutingDeliverySignals>> RoutingDeliveriesTask)> LoadEndpointsAsync(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        var hub = await GetIoTHubGenericResourceAsync(hubName, resourceGroup, subscription, tenant, retryPolicy, cancellationToken);
        var properties = hub.Properties?.ToObjectFromJson(IoTHubJsonContext.Default.IoTHubProperties);
        var endpoints = ConvertToRoutingEndpointDetailsList(properties?.Routing?.Endpoints);

        if (!string.IsNullOrWhiteSpace(endpointName))
        {
            endpoints = endpoints
                .Where(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (endpoints.Count == 0)
            {
                throw new KeyNotFoundException($"Routing endpoint '{endpointName}' not found on IoT Hub '{hubName}'.");
            }
        }

        var healthMapTask = GetEndpointHealthMapAsync(hub.Id, properties?.Routing?.Endpoints, tenant, cancellationToken);
        var routingDeliveriesTask = QueryAllRoutingDeliveriesAsync(
            hub.Id, window, interval, tenant, retryPolicy, cancellationToken);
        return (endpoints, hub.Id, healthMapTask, routingDeliveriesTask);
    }

    private async Task<GenericResourceData> GetIoTHubGenericResourceAsync(
        string hubName,
        string resourceGroup,
        string subscription,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        var subscriptionResource = await AzureService.GetSubscription(subscription, tenant, retryPolicy, cancellationToken);
        var armClient = await CreateArmClientAsync(tenant, retryPolicy, cancellationToken: cancellationToken);
        var iotHubResourceId = BuildResourceId(
            subscriptionResource.Data.SubscriptionId, resourceGroup, "Microsoft.Devices/IotHubs", hubName);
        var hub = await armClient.GetGenericResource(iotHubResourceId).GetAsync(cancellationToken);

        return hub.Value.Data;
    }

    private static IoTHubDescription ConvertToIoTHubDescription(GenericResourceData hub)
    {
        var properties = hub.Properties?.ToObjectFromJson(IoTHubJsonContext.Default.IoTHubProperties);

        return new IoTHubDescription(
            hub.Id.ToString(),
            hub.Name,
            hub.Location.ToString(),
            hub.Id?.ResourceGroupName ?? string.Empty,
            hub.Id?.SubscriptionId ?? string.Empty,
            hub.Sku?.Name ?? string.Empty,
            hub.Sku?.Capacity ?? 0,
            properties?.State ?? string.Empty,
            properties?.HostName ?? string.Empty,
            ConvertToRoutingEndpointDetailsList(properties?.Routing?.Endpoints)
        );
    }

    private static List<RoutingEndpointDetails> ConvertToRoutingEndpointDetailsList(RoutingEndpoints? endpoints)
    {
        if (endpoints == null)
        {
            return [];
        }

        var results = new List<RoutingEndpointDetails>();
        AppendEndpoints(results, endpoints.EventHubs, "EventHub", isContainer: false);
        AppendEndpoints(results, endpoints.ServiceBusQueues, "ServiceBusQueue", isContainer: false);
        AppendEndpoints(results, endpoints.ServiceBusTopics, "ServiceBusTopic", isContainer: false);
        AppendEndpoints(results, endpoints.StorageContainers, "StorageContainer", isContainer: true);
        AppendEndpoints(results, endpoints.CosmosDBSqlContainers, "CosmosDBSqlContainer", isContainer: true);
        return results;
    }

    // Projects one routing custom-endpoint kind onto RoutingEndpointDetails. Container kinds (Storage/Cosmos)
    // carry ContainerName (Cosmos also a DatabaseName) and resolve the resource name from the endpoint URI;
    // the messaging kinds (Event Hubs / Service Bus) carry EntityPath.
    private static void AppendEndpoints(List<RoutingEndpointDetails> results, List<RoutingEndpointProperties>? endpoints, string endpointType, bool isContainer)
    {
        foreach (var e in endpoints ?? [])
        {
            results.Add(new RoutingEndpointDetails(
                e.Name ?? string.Empty,
                endpointType,
                isContainer
                    ? GetEndpointResourceName(e.Id, e.EndpointUri, e.ContainerName, preferEndpointUri: true)
                    : GetEndpointResourceName(e.Id, e.EndpointUri, e.EntityPath),
                e.SubscriptionId,
                e.ResourceGroup,
                e.EndpointUri,
                isContainer ? null : e.EntityPath,
                isContainer ? e.ContainerName : null,
                endpointType == "CosmosDBSqlContainer" ? e.DatabaseName : null,
                e.AuthenticationType,
                e.BatchFrequencyInSeconds));
        }
    }

    // Every routing custom-endpoint kind flattened into one sequence (order is not significant).
    private static IEnumerable<RoutingEndpointProperties> EnumerateEndpoints(RoutingEndpoints endpoints)
    {
        foreach (var list in new[] { endpoints.EventHubs, endpoints.ServiceBusQueues, endpoints.ServiceBusTopics, endpoints.StorageContainers, endpoints.CosmosDBSqlContainers })
        {
            foreach (var e in list ?? [])
            {
                yield return e;
            }
        }
    }

    // Fetches the IoT Hub routingEndpointsHealth signals and re-keys them by endpoint name (the REST
    // API keys by routing endpoint id, a GUID).
    private async Task<IReadOnlyDictionary<string, EndpointHealthData>> GetEndpointHealthMapAsync(
        ResourceIdentifier hubResourceId,
        RoutingEndpoints? routingEndpoints,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var healthById = await GetRoutingEndpointsHealthAsync(hubResourceId, tenant, cancellationToken);
        var idToName = BuildEndpointIdToNameMap(routingEndpoints);
        var map = new Dictionary<string, EndpointHealthData>(StringComparer.OrdinalIgnoreCase);
        foreach (var (endpointId, data) in healthById)
        {
            if (idToName.TryGetValue(endpointId, out var name) && !string.IsNullOrEmpty(name))
            {
                map[name] = data;
            }
        }

        return map;
    }

    // Base per-endpoint signals shared by all three routing commands, read from the two hub-wide lookups:
    // the hub's own RoutingDeliveries counts + RoutingDeliveryLatency (and hourly trend), plus the
    // routingEndpointsHealth status and the derived send-to-success latency. No per-endpoint or
    // target-resource Azure Monitor query is issued here.
    private static async Task<EndpointBaseSignals> GetBaseSignalsAsync(
        RoutingEndpointDetails endpoint,
        Task<IReadOnlyDictionary<string, RoutingDeliverySignals>> routingDeliveriesTask,
        Task<IReadOnlyDictionary<string, EndpointHealthData>> endpointHealthMapTask)
    {
        // Source 2: AzMon on the IoT Hub — one hub-wide query for all endpoints (split by EndpointName).
        var routingMap = await routingDeliveriesTask;
        routingMap.TryGetValue(endpoint.Name, out var rd);

        // Source 1: IoT Hub routing endpoint health (status + send timestamps), also a single hub-wide lookup.
        var healthMap = await endpointHealthMapTask;
        healthMap.TryGetValue(endpoint.Name, out var hubHealth);
        var lastKnownErrorTime = ParseRfc1123OrNull(hubHealth?.LastKnownErrorTime);
        var lastSendAttemptTime = ParseRfc1123OrNull(hubHealth?.LastSendAttemptTime);
        var lastSuccessfulSendAttemptTime = ParseRfc1123OrNull(hubHealth?.LastSuccessfulSendAttemptTime);
        var sendToSuccessLatency = ComputeSendToSuccessLatency(lastSendAttemptTime, lastSuccessfulSendAttemptTime);

        return new EndpointBaseSignals(
            rd?.Deliveries,
            rd?.Failures,
            rd?.Latency,
            rd?.PeakLatency,
            rd?.LatencyTrend ?? [],
            hubHealth,
            sendToSuccessLatency,
            lastKnownErrorTime,
            lastSendAttemptTime);
    }

    // Lightweight endpoint-health verdict from the hub's own routing signals. Returns "unavailable" when
    // the target resource (or its routed sub-entity) is missing, "degraded" on any in-window failure
    // signal, "healthy" when there are in-window routing deliveries with no failure, else "unreported".
    private async Task<string> ComputeHealthStatusAsync(
        RoutingEndpointDetails endpoint,
        EndpointBaseSignals signals,
        ObservationWindow window,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var flags = EvaluateFailureFlags(signals, window);
        var latencyThresholdExceeded = IsLatencyThresholdExceeded(endpoint, signals, window);

        // ARM resolution is authoritative: if the target resource (or its routed sub-entity) does not exist,
        // the endpoint is unavailable regardless of any historical routing deliveries in the window.
        if (await CheckTargetResourceMissingAsync(endpoint, tenant, cancellationToken) != null)
        {
            return "unavailable";
        }

        var failureSignal = flags.HubStatusIsFailure
            || flags.HasRoutedFailures
            || latencyThresholdExceeded
            || HasErrorInWindow(signals.HubHealth?.LastKnownError, signals.LastKnownErrorTime, window);

        if (failureSignal)
        {
            return "degraded";
        }

        // No routing activity in the window; there is nothing to judge, so it is reported as unreported.
        return flags.HasRoutedDeliveries ? "healthy" : "unreported";
    }

    // Full endpoint diagnosis: always runs the target-resource Azure Monitor deep dive, attributes a likely
    // fault domain with a confidence and evidence narrative, and reports a health verdict of unavailable,
    // degraded, healthy, or unreported. Returns the health verdict plus the raw target-resource signals.
    private async Task<(RoutingEndpointHealth Health, RoutingTargetResourceSignals? TargetSignals, RoutingTargetConfigurationSignals? TargetConfiguration)> DiagnoseEndpointHealthAsync(
        RoutingEndpointDetails endpoint,
        EndpointBaseSignals signals,
        Task<TargetResourceHealth> targetHealthTask,
        Task<RoutingTargetConfigurationSignals?> targetConfigurationTask,
        ObservationWindow window,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var iotHubStatus = signals.HubHealth?.HealthStatus;
        var routingLatency = signals.RoutingLatency;
        var sendToSuccessLatency = signals.SendToSuccessLatency;
        var flags = EvaluateFailureFlags(signals, window);
        var hasRecentError = HasErrorInWindow(
            signals.HubHealth?.LastKnownError,
            signals.LastKnownErrorTime,
            window);

        // Unavailable: the ARM probe asserts the target resource (or its routed sub-entity) does not resolve.
        // The routing latency signals are stale/meaningless, so report the verdict only and stop here.
        var missingDetail = await CheckTargetResourceMissingAsync(endpoint, tenant, cancellationToken);
        if (missingDetail != null)
        {
            // The resource is gone; observe the deep-dive task so it can't fault unobserved, then discard it.
            try
            { await targetHealthTask; }
            catch (Exception ex) { _logger.LogDebug(ex, "Ignoring target metrics for unavailable endpoint '{EndpointName}'.", endpoint.Name); }
            try
            { await targetConfigurationTask; }
            catch (Exception ex) { _logger.LogDebug(ex, "Ignoring target configuration for unavailable endpoint '{EndpointName}'.", endpoint.Name); }

            var unavailableHealth = new RoutingEndpointHealth(
                EndpointHealthStatus: "unavailable",
                RoutingDeliveryLatencyMsAvg: null,
                RoutingDeliveryLatencyMsPeak: null,
                LatencyThresholdMs: GetLatencyThresholdMilliseconds(endpoint),
                RoutedDeliverySuccess: signals.RoutedDeliveries,
                RoutedDeliveryFailures: signals.RoutedFailures,
                SendToSuccessLatencyMs: null,
                ImpactDetails: new RoutingEndpointImpactDetails(
                    ConfidenceScore: 1.0,
                    LikelyFaultDomain: RoutingFaultDomain.TargetUnavailable,
                    LikelyFaultDetail: char.ToUpperInvariant(missingDetail[0]) + missingDetail[1..] + "."));
            return (unavailableHealth, null, null);
        }

        // Only an in-window send-to-success lag is treated as current; a stale value from before the
        // window does not count toward the latency-elevated narrative.
        var inWindowSendLatency = window.Contains(signals.LastSendAttemptTime) ? sendToSuccessLatency : null;
        var peakRoutingLatency = signals.PeakRoutingLatency ?? routingLatency;
        var maxLatency = Math.Max(inWindowSendLatency ?? 0, peakRoutingLatency ?? 0);
        var latencyThreshold = GetLatencyThresholdMilliseconds(endpoint);
        var latencyThresholdExceeded = maxLatency >= latencyThreshold;

        // The deep dive was started concurrently with the base signals above; collect it now.
        var (successfulRequests, serverErrors, userErrors, throttledRequests, faultDetail, errorBreakdown) = await targetHealthTask;
        var targetConfiguration = await targetConfigurationTask;

        // Access-denied is a distinct root cause: the hub reports Unauthorized/Forbidden, or the target
        // logged authorization/authentication errors. It is not a generic client (4xx) error.
        var targetAccessDenied = IndicatesAccessDenied(signals.HubHealth?.LastKnownError, errorBreakdown);
        var targetNetworkRestricted = targetConfiguration?.Warnings?.Count > 0;

        var (confidence, faultDomain) = AnalyzeFaultDomain(
            flags.HubStatusIsFailure ? iotHubStatus : hasRecentError ? "unhealthy" : null,
            sendToSuccessLatency, latencyThresholdExceeded,
            signals.RoutedFailures, serverErrors, userErrors, throttledRequests, targetAccessDenied, targetNetworkRestricted);

        // Degraded is decided by per-endpoint signals only (hub health status or routing delivery
        // failures). Target ServerErrors/UserErrors/ThrottledRequests are parent-resource (namespace/
        // account) level metrics shared by every endpoint on that resource, so they cannot mark this
        // endpoint degraded on their own - otherwise a sibling entity's errors would blame a healthy endpoint.
        var isDegraded = flags.HubStatusIsFailure || flags.HasRoutedFailures || latencyThresholdExceeded || hasRecentError;
        var status = isDegraded ? "degraded" : flags.HasRoutedDeliveries ? "healthy" : "unreported";

        var faultNarrative = BuildFaultDetail(
            endpoint.EndpointType, status, iotHubStatus, flags.HubStatusIsFailure,
            signals.HubHealth?.LastKnownError, hasRecentError,
            signals.RoutedFailures, sendToSuccessLatency, peakRoutingLatency, latencyThresholdExceeded,
            latencyThreshold, serverErrors, userErrors, throttledRequests, errorBreakdown, faultDetail);

        // Raw parent/target-resource metrics, surfaced as-is (not per-endpoint attributed) so downstream
        // reasoning has the full error/throttle picture even when this endpoint itself did not fail.
        var targetResourceSignals = new RoutingTargetResourceSignals(
            successfulRequests,
            serverErrors,
            userErrors,
            throttledRequests,
            errorBreakdown.Count == 0 ? null : errorBreakdown,
            faultDetail);

        var health = new RoutingEndpointHealth(
            EndpointHealthStatus: status,
            RoutingDeliveryLatencyMsAvg: routingLatency,
            RoutingDeliveryLatencyMsPeak: peakRoutingLatency,
            LatencyThresholdMs: latencyThreshold,
            RoutedDeliverySuccess: signals.RoutedDeliveries,
            RoutedDeliveryFailures: signals.RoutedFailures,
            SendToSuccessLatencyMs: sendToSuccessLatency,
            ImpactDetails: new RoutingEndpointImpactDetails(
                signals.LatencyTrend.Count == 0 ? null : signals.LatencyTrend,
                confidence,
                faultDomain,
                faultNarrative));

        return (health, targetResourceSignals, targetConfiguration);
    }

    // In-window hub failure status plus whether the endpoint had routing successes/failures. Shared by the
    // health verdict and the full diagnosis.
    private static (bool HubStatusIsFailure, bool HasRoutedDeliveries, bool HasRoutedFailures) EvaluateFailureFlags(
        EndpointBaseSignals signals, ObservationWindow window)
    {
        var hubStatusIsFailure = IsFailureStatus(signals.HubHealth?.HealthStatus)
            && window.Contains(signals.LastKnownErrorTime ?? signals.LastSendAttemptTime);
        return (hubStatusIsFailure, (signals.RoutedDeliveries ?? 0) > 0, (signals.RoutedFailures ?? 0) > 0);
    }

    private const double DefaultLatencyThresholdMs = 300_000d;

    // Base per-endpoint routing signals shared by the health, latency, and diagnose commands.
    private sealed record EndpointBaseSignals(
        double? RoutedDeliveries,
        double? RoutedFailures,
        double? RoutingLatency,
        double? PeakRoutingLatency,
        IReadOnlyList<LatencyTrendPoint> LatencyTrend,
        EndpointHealthData? HubHealth,
        double? SendToSuccessLatency,
        DateTimeOffset? LastKnownErrorTime,
        DateTimeOffset? LastSendAttemptTime);

    // Target-resource Azure Monitor deep-dive result (positional so it deconstructs like the prior tuple).
    private sealed record TargetResourceHealth(
        double? SuccessfulRequests,
        double? ServerErrors,
        double? UserErrors,
        double? ThrottledRequests,
        string? Error,
        IReadOnlyDictionary<string, double> ErrorBreakdown);

    // Per-endpoint hub RoutingDeliveries counts and RoutingDeliveryLatency (avg + hourly trend).
    private sealed record RoutingDeliverySignals(
        double? Deliveries,
        double? Failures,
        double? Latency,
        double? PeakLatency,
        IReadOnlyList<LatencyTrendPoint> LatencyTrend);

    private sealed record ArmGetResult(HttpStatusCode StatusCode, string Content)
    {
        public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
    }

    private sealed record RoutingTargetDescriptor(
        string ResourceType,
        string ParentNoun,
        string ResourceLabel,
        string ApiVersion,
        string[] MetricNames,
        string CommandMetrics,
        string Aggregation);

    private static readonly RoutingTargetDescriptor s_eventHubTarget = new(
        "Microsoft.EventHub/namespaces", "Event Hubs namespace", "target Event Hub", "2024-01-01",
        ["ServerErrors", "UserErrors", "ThrottledRequests", "QuotaExceededErrors"],
        "ServerErrors,UserErrors,ThrottledRequests,QuotaExceededErrors", "Total");
    private static readonly RoutingTargetDescriptor s_serviceBusTarget = new(
        "Microsoft.ServiceBus/namespaces", "Service Bus namespace", "target Service Bus", "2024-01-01",
        ["ServerErrors", "UserErrors", "ThrottledRequests"],
        "ServerErrors,UserErrors,ThrottledRequests", "Total");
    private static readonly RoutingTargetDescriptor s_storageTarget = new(
        "Microsoft.Storage/storageAccounts", "Storage account", "target Storage account", "2023-05-01",
        ["Transactions"], "Transactions", "Total");
    private static readonly RoutingTargetDescriptor s_cosmosTarget = new(
        "Microsoft.DocumentDB/databaseAccounts", "Cosmos DB account", "target Cosmos DB", "2024-05-15",
        ["TotalRequests", "NormalizedRUConsumption"], "TotalRequests", "Count");

    private static RoutingTargetDescriptor? GetTargetDescriptor(string? endpointType) => endpointType switch
    {
        "EventHub" => s_eventHubTarget,
        "ServiceBusQueue" or "ServiceBusTopic" => s_serviceBusTarget,
        "StorageContainer" => s_storageTarget,
        "CosmosDBSqlContainer" => s_cosmosTarget,
        _ => null
    };

    internal static double GetLatencyThresholdMilliseconds(RoutingEndpointDetails endpoint)
    {
        var storageBatchThreshold = endpoint.EndpointType == "StorageContainer"
            ? (endpoint.BatchFrequencyInSeconds ?? 0) * 2_000d
            : 0;
        return Math.Max(DefaultLatencyThresholdMs, storageBatchThreshold);
    }

    private static bool IsLatencyThresholdExceeded(
        RoutingEndpointDetails endpoint,
        EndpointBaseSignals signals,
        ObservationWindow window)
    {
        var sendToSuccessLatency = window.Contains(signals.LastSendAttemptTime)
            ? signals.SendToSuccessLatency
            : null;
        var worstLatency = Math.Max(sendToSuccessLatency ?? 0, signals.PeakRoutingLatency ?? signals.RoutingLatency ?? 0);
        return worstLatency >= GetLatencyThresholdMilliseconds(endpoint);
    }

    // True when the failure is an access-denied problem: the hub's per-endpoint last-known error names an
    // authorization/authentication failure, or the target's error breakdown is dominated by auth response
    // types / 401 / 403. Distinguishes a missing RBAC role from a generic client error.
    private static bool IndicatesAccessDenied(string? hubLastKnownError, IReadOnlyDictionary<string, double> errorBreakdown)
    {
        if (!string.IsNullOrEmpty(hubLastKnownError))
        {
            string[] tokens = ["Unauthorized", "Forbidden", "Authorization", "Authentication", "AccessDenied", "Access denied", "403", "401"];
            if (tokens.Any(t => hubLastKnownError.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (errorBreakdown.Count > 0)
        {
            double authTotal = 0, total = 0;
            foreach (var (key, value) in errorBreakdown)
            {
                total += value;
                if (key.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("403", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("401", StringComparison.OrdinalIgnoreCase))
                {
                    authTotal += value;
                }
            }

            if (total > 0 && authTotal / total > 0.5)
            {
                return true;
            }
        }

        return false;
    }

    // Attribute degraded health to a likely root cause. Target-side errors identify the cause when
    // present; IoT Hub routing delivery failures are usually a downstream symptom of a throttled or
    // erroring target, so they only drive attribution when there are no meaningful target errors.
    // Throttling takes precedence: when a target throttles, the co-occurring user errors are typically
    // the 429/ServerBusy rejections, so they are weighed together as throttling evidence.
    internal static (double? Confidence, string FaultDomain) AnalyzeFaultDomain(
        string? iotHubStatus,
        double? sendToSuccessLatency,
        bool latencyThresholdExceeded,
        double? routedFailures,
        double? serverErrors,
        double? userErrors,
        double? throttledRequests,
        bool targetAccessDenied = false,
        bool targetNetworkRestricted = false)
    {
        // Minimum number of observed errors before an attribution is trusted.
        const double MinFailureEvidence = 10;

        var delivery = routedFailures ?? 0;   // IoT Hub -> endpoint delivery failures (often downstream)
        var throttle = throttledRequests ?? 0;
        var server = serverErrors ?? 0;
        var user = userErrors ?? 0;
        var targetTotal = throttle + server + user;
        var hardStatus = IsFailureStatus(iotHubStatus);

        if (targetNetworkRestricted && (delivery > 0 || hardStatus || targetTotal > 0))
        {
            return (0.9, RoutingFaultDomain.TargetNetwork);
        }

        // Access denied is a distinct, highly-actionable root cause (usually missing RBAC) - not a generic
        // client (4xx) error - so it takes precedence when the endpoint is failing.
        if (targetAccessDenied && (delivery > 0 || hardStatus || targetTotal > 0))
        {
            return (0.9, RoutingFaultDomain.TargetAuthorization);
        }

        // Target-side error metrics are parent-resource scoped (shared across sibling endpoints on the
        // same namespace/account), so only attribute them to THIS endpoint when it has its own failure
        // evidence - routing delivery failures or a hub failure status. A fully-successful endpoint is
        // never blamed for a sibling entity's errors.
        if (targetTotal > 0 && (delivery > 0 || hardStatus))
        {
            // Group throttling with its accompanying user-error rejections (429/ServerBusy).
            var throttleGroup = throttle > 0 ? throttle + user : 0;

            string domain;
            double dominant;
            if (throttleGroup > server && throttleGroup > 0)
            {
                domain = RoutingFaultDomain.TargetThrottling;
                dominant = throttleGroup;
            }
            else
            {
                // No throttling (or server errors dominate): pick the larger of server vs. plain user errors.
                var plainUser = throttle > 0 ? 0 : user;
                if (server >= plainUser)
                {
                    domain = RoutingFaultDomain.TargetServerError;
                    dominant = server;
                }
                else
                {
                    domain = RoutingFaultDomain.TargetUserError;
                    dominant = plainUser;
                }
            }

            // Proportion measures how dominant the leading category is (ambiguity); the reported confidence
            // additionally discounts a small sample. They must stay separate: a 100%-one-category signal from
            // a few errors is unambiguous (just low confidence), not an ambiguous split.
            var proportion = dominant / targetTotal;
            var confidence = Math.Round(proportion, 2);
            if (targetTotal < MinFailureEvidence)
            {
                // Few errors: keep the attribution but do not present it as high confidence.
                confidence = Math.Min(confidence, 0.5);
            }

            // No clear majority for a non-throttling signal is inconclusive (throttling stays actionable).
            // Gate on the proportion, not the sample-discounted confidence, so a clear-but-small signal keeps
            // its attribution instead of collapsing to IoTHubDelivery/Inconclusive.
            if (proportion <= 0.5 && domain != RoutingFaultDomain.TargetThrottling)
            {
                return hardStatus
                    ? (0.5, RoutingFaultDomain.IoTHubDelivery)
                    : (confidence, RoutingFaultDomain.Inconclusive);
            }

            return (confidence, domain);
        }

        // No meaningful target errors: a hub failure status or a strong routing-failure count points at
        // the IoT Hub delivery side.
        if (hardStatus || delivery >= MinFailureEvidence)
        {
            return (delivery >= MinFailureEvidence ? 0.7 : 0.5, RoutingFaultDomain.IoTHubDelivery);
        }

        // Weak, unattributable evidence (a few routing failures or a latency blip on its own).
        if (delivery > 0 || latencyThresholdExceeded || (sendToSuccessLatency ?? 0) > 0)
        {
            return (0.3, RoutingFaultDomain.Inconclusive);
        }

        return (null, RoutingFaultDomain.None);
    }

    // Builds the human-readable LikelyFaultDetail narrative for a deep-dived endpoint, spelling out the
    // evidence (hub status, routing failures, target errors, latency). LikelyFaultDomain and ConfidenceScore
    // carry the structured attribution, so this text stays evidence-only. Returns null when there is nothing
    // noteworthy to report (e.g. a clean deep dive that only confirmed health).
    private static string? BuildFaultDetail(
        string? endpointType,
        string? endpointHealthStatus,
        string? iotHubStatus,
        bool hubStatusIsFailure,
        string? lastKnownError,
        bool hasRecentError,
        double? routedFailures,
        double? sendToSuccessLatency,
        double? routingLatency,
        bool latencyThresholdExceeded,
        double thresholdMilliseconds,
        double? serverErrors,
        double? userErrors,
        double? throttledRequests,
        IReadOnlyDictionary<string, double> errorBreakdown,
        string? faultDetail)
    {
        var thresholdSeconds = thresholdMilliseconds / 1000d;

        if (IsFailureStatus(endpointHealthStatus))
        {
            var reasons = new List<string>();
            if (hubStatusIsFailure)
            {
                reasons.Add($"IoT Hub reports the endpoint as '{iotHubStatus}'"
                    + (string.IsNullOrEmpty(lastKnownError) ? "" : $" (last error: {lastKnownError})"));
            }
            else if (hasRecentError)
            {
                reasons.Add($"IoT Hub reported a recent endpoint error: {lastKnownError}");
            }
            if ((routedFailures ?? 0) > 0)
            {
                reasons.Add($"{routedFailures ?? 0:0} routing delivery failure(s) from the hub to the endpoint");
            }

            // Prefer the detailed per-dimension error breakdown from the target resource metrics; it names
            // the specific response types / status codes behind the failure. Fall back to the aggregates.
            if (errorBreakdown.Count > 0)
            {
                var detail = string.Join(", ", errorBreakdown
                    .OrderByDescending(entry => entry.Value)
                    .Take(4)
                    .Select(entry => $"{entry.Key}={entry.Value:0}"));
                reasons.Add($"{GetTargetDescriptor(endpointType)?.ResourceLabel ?? "target resource"} reported {detail}");
            }
            else
            {
                if ((throttledRequests ?? 0) > 0)
                {
                    reasons.Add($"{throttledRequests ?? 0:0} throttled request(s) on the target resource");
                }
                if ((serverErrors ?? 0) > 0)
                {
                    reasons.Add($"{serverErrors ?? 0:0} server error(s) on the target resource");
                }
                if ((userErrors ?? 0) > 0)
                {
                    reasons.Add($"{userErrors ?? 0:0} user error(s) on the target resource");
                }
            }

            if (latencyThresholdExceeded)
            {
                var worst = Math.Max(sendToSuccessLatency ?? 0, routingLatency ?? 0);
                reasons.Add($"routing latency {worst:0}ms met/exceeded the {thresholdSeconds:0}s threshold");
            }
            if (!string.IsNullOrEmpty(faultDetail))
            {
                reasons.Add(faultDetail);
            }
            if (reasons.Count == 0)
            {
                reasons.Add("a failure signal was reported without further detail");
            }

            var detailText = string.Join("; ", reasons) + ".";
            detailText = char.ToUpperInvariant(detailText[0]) + detailText[1..];

            // Service Bus / Event Hub metrics report only coarse UserErrors (4xx) / ServerErrors (5xx)
            // counts with no error-reason dimension, so call out the classes that are not throttling and
            // point at diagnostic logs for the exact cause (e.g. authorization). Storage/Cosmos already
            // name specifics (ResponseType / StatusCode) in the breakdown, so this note is skipped there.
            if (endpointType is "EventHub" or "ServiceBusQueue" or "ServiceBusTopic")
            {
                var throttleCount = throttledRequests ?? 0;
                var serverCount = serverErrors ?? 0;
                var nonThrottleUser = throttleCount > 0 ? Math.Max(0, (userErrors ?? 0) - throttleCount) : (userErrors ?? 0);
                var classes = new List<string>();
                if (nonThrottleUser > 0)
                {
                    classes.Add($"{nonThrottleUser:0} client (4xx) error(s) not explained by throttling (e.g. authorization or malformed requests)");
                }
                if (serverCount > 0)
                {
                    classes.Add($"{serverCount:0} server (5xx) error(s) on the target");
                }
                if (classes.Count > 0)
                {
                    detailText += $" Non-throttling errors: {string.Join("; ", classes)}. Azure Monitor metrics do not name the exact reason \u2014 inspect the target resource's diagnostic/operational logs for specifics.";
                }
            }

            return detailText;
        }

        // Not degraded: the only detail worth noting is a latency that met/exceeded the threshold.
        if (latencyThresholdExceeded)
        {
            var worst = Math.Max(sendToSuccessLatency ?? 0, routingLatency ?? 0);
            return $"Routing latency {worst:0}ms is at or above the {thresholdSeconds:0}s threshold.";
        }

        return null;
    }

    // Builds fault-aware drill-down aids for an endpoint: the resolved target resource id and a set of
    // ready-to-run `azmcp` commands chosen for the likely fault domain, favoring information the diagnosis
    // does not already carry (RBAC assignments, resource existence) over re-querying summarized metrics.
    internal static RoutingEndpointExploration BuildExploration(
        RoutingEndpointDetails endpoint,
        ResourceIdentifier hubResourceId,
        RoutingEndpointHealth? health,
        ObservationWindow window,
        TimeSpan interval)
    {
        const string hubType = "Microsoft.Devices/IotHubs";
        var hubSub = hubResourceId.SubscriptionId;
        var hubRg = hubResourceId.ResourceGroupName;
        var hubName = hubResourceId.Name;
        var faultDomain = health?.ImpactDetails?.LikelyFaultDomain;

        var (parent, _, _, _) = BuildTargetResourceIds(endpoint);
        var target = GetTargetDescriptor(endpoint.EndpointType);
        var targetResolvable = parent is not null && target is not null
            && !string.IsNullOrWhiteSpace(endpoint.SubscriptionId)
            && !string.IsNullOrWhiteSpace(endpoint.ResourceGroup)
            && !string.IsNullOrWhiteSpace(endpoint.EndpointResourceName);

        var hubDeliveries = BuildMetricsQueryCommand(
            hubSub, hubRg, hubName, hubType, "RoutingDeliveries", "Total", null, window, interval);
        var hubLatency = BuildMetricsQueryCommand(
            hubSub, hubRg, hubName, hubType, "RoutingDeliveryLatency", "Average", null, window, interval);

        string? targetMetrics = null, roleAssignments = null, resourceList = null;
        if (targetResolvable)
        {
            targetMetrics = BuildMetricsQueryCommand(
                endpoint.SubscriptionId!, endpoint.ResourceGroup!, endpoint.EndpointResourceName!,
                target!.ResourceType, target.CommandMetrics, target.Aggregation, null,
                window, interval);
            roleAssignments = BuildRoleAssignmentListCommand(endpoint.SubscriptionId!, parent!.ToString());
            resourceList = BuildResourceListCommand(endpoint.SubscriptionId!, endpoint.ResourceGroup!);
        }

        var metricsQueried = new List<string>();
        var drillDown = new List<string>();

        void AddTargetMetrics()
        {
            if (targetMetrics is not null)
            {
                drillDown.Add(targetMetrics);
                metricsQueried.AddRange(target!.MetricNames);
            }
        }

        void AddHubMetrics(bool includeLatency)
        {
            drillDown.Add(hubDeliveries);
            metricsQueried.Add("RoutingDeliveries");
            if (includeLatency)
            {
                drillDown.Add(hubLatency);
                metricsQueried.Add("RoutingDeliveryLatency");
            }
        }

        switch (faultDomain)
        {
            // The target (or its sub-entity) does not resolve: confirm whether it still exists and whether
            // the hub identity can reach it (a 403 can surface as not-found). Metrics are moot here.
            case RoutingFaultDomain.TargetUnavailable:
                if (resourceList is not null)
                    drillDown.Add(resourceList);
                if (roleAssignments is not null)
                    drillDown.Add(roleAssignments);
                break;
            // The hub identity is denied access to the target: check its RBAC role assignment.
            case RoutingFaultDomain.TargetAuthorization:
                if (roleAssignments is not null)
                    drillDown.Add(roleAssignments);
                if (resourceList is not null)
                    drillDown.Add(resourceList);
                break;
            // Network configuration evidence already appears in the diagnosis; use metrics to correlate
            // the blocked requests without suggesting that the endpoint's RBAC role is missing.
            case RoutingFaultDomain.TargetNetwork:
                AddTargetMetrics();
                AddHubMetrics(includeLatency: false);
                break;
            // 4xx errors are usually a missing/incorrect role for the hub identity on the target.
            case RoutingFaultDomain.TargetUserError:
                if (roleAssignments is not null)
                    drillDown.Add(roleAssignments);
                AddTargetMetrics();
                break;
            // Throttling / server errors: the target metric trend shows how the pressure evolved.
            case RoutingFaultDomain.TargetThrottling:
            case RoutingFaultDomain.TargetServerError:
                AddTargetMetrics();
                break;
            // Failures on the hub delivery leg, not visible as target-side errors (e.g. the hub identity
            // cannot authenticate or reach the target): inspect the hub routing metrics and check RBAC.
            case RoutingFaultDomain.IoTHubDelivery:
                AddHubMetrics(includeLatency: true);
                if (roleAssignments is not null)
                    drillDown.Add(roleAssignments);
                break;
            // Inconclusive / Unknown / None: give a broad starting set, including an RBAC check.
            default:
                AddTargetMetrics();
                if (roleAssignments is not null)
                    drillDown.Add(roleAssignments);
                AddHubMetrics(includeLatency: false);
                break;
        }

        // Always leave at least one actionable command, even when the target could not be resolved.
        if (drillDown.Count == 0)
        {
            AddHubMetrics(includeLatency: true);
        }

        return new RoutingEndpointExploration(
            parent?.ToString(),
            metricsQueried.Count == 0 ? null : metricsQueried,
            drillDown);
    }

    private static string BuildRoleAssignmentListCommand(string? subscription, string scope) =>
        $"azmcp role assignment list --subscription {subscription} --scope {scope}";

    private static string BuildResourceListCommand(string? subscription, string? resourceGroup) =>
        $"azmcp group resource list --subscription {subscription} --resource-group {resourceGroup}";

    private static string BuildMetricsQueryCommand(
        string? subscription, string? resourceGroup, string resource, string resourceType,
        string metricNames, string aggregation, string? filter,
        ObservationWindow window, TimeSpan interval)
    {
        var command =
            $"azmcp monitor metrics query --subscription {subscription} --resource-group {resourceGroup} " +
            $"--resource {resource} --resource-type {resourceType} --metric-namespace {resourceType} " +
            // metric-names is comma-separated with no spaces, so it is left unquoted to stay copy-paste safe.
            $"--metric-names {metricNames} --aggregation {aggregation} " +
            $"--start-time {ToIsoString(window.StartTime)} --end-time {ToIsoString(window.EndTime)} " +
            $"--interval {XmlConvert.ToString(interval)}";
        if (!string.IsNullOrEmpty(filter))
        {
            command += $" --filter \"{filter}\"";
        }

        return command;
    }

    // Builds the ARM ids for the endpoint's target parent (account/namespace) and its routed sub-entity
    // (Service Bus queue/topic, Event Hub, blob container, Cosmos container), when identifiable.
    private static (ResourceIdentifier? Parent, ResourceIdentifier? Entity, string? EntityKind, string? EntityName) BuildTargetResourceIds(RoutingEndpointDetails e)
    {
        var target = GetTargetDescriptor(e.EndpointType);
        if (target is null ||
            string.IsNullOrWhiteSpace(e.SubscriptionId) ||
            string.IsNullOrWhiteSpace(e.ResourceGroup) ||
            string.IsNullOrWhiteSpace(e.EndpointResourceName))
        {
            return (null, null, null, null);
        }

        var parent = BuildResourceId(e.SubscriptionId!, e.ResourceGroup!, target.ResourceType, e.EndpointResourceName!);

        (ResourceIdentifier Entity, string Kind, string Name)? entity = e.EndpointType switch
        {
            "EventHub" when !string.IsNullOrWhiteSpace(e.EntityPath)
                => (new ResourceIdentifier($"{parent}/eventhubs/{e.EntityPath}"), "event hub", e.EntityPath!),
            "ServiceBusQueue" when !string.IsNullOrWhiteSpace(e.EntityPath)
                => (new ResourceIdentifier($"{parent}/queues/{e.EntityPath}"), "queue", e.EntityPath!),
            "ServiceBusTopic" when !string.IsNullOrWhiteSpace(e.EntityPath)
                => (new ResourceIdentifier($"{parent}/topics/{e.EntityPath}"), "topic", e.EntityPath!),
            "StorageContainer" when !string.IsNullOrWhiteSpace(e.ContainerName)
                => (new ResourceIdentifier($"{parent}/blobServices/default/containers/{e.ContainerName}"), "blob container", e.ContainerName!),
            "CosmosDBSqlContainer" when !string.IsNullOrWhiteSpace(e.ContainerName) && !string.IsNullOrWhiteSpace(e.DatabaseName)
                => (new ResourceIdentifier($"{parent}/sqlDatabases/{e.DatabaseName}/containers/{e.ContainerName}"), "container", e.ContainerName!),
            _ => null
        };

        return (parent, entity?.Entity, entity?.Kind, entity?.Name);
    }

    private static string GetTargetCacheKey(RoutingEndpointDetails endpoint)
    {
        var (parent, _, _, _) = BuildTargetResourceIds(endpoint);
        return parent?.ToString() ?? $"endpoint:{endpoint.Name}";
    }

    // Returns a not-found explanation if the endpoint's target resource, or its routed sub-entity, does
    // not exist; otherwise null (it exists, or existence could not be determined, e.g. missing RBAC).
    private async Task<string?> CheckTargetResourceMissingAsync(
        RoutingEndpointDetails endpoint,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var (parent, entity, entityKind, entityName) = BuildTargetResourceIds(endpoint);
        var target = GetTargetDescriptor(endpoint.EndpointType);
        if (parent is null || target is null)
        {
            return null;
        }

        var parentNoun = target.ParentNoun;
        var location = $"subscription {endpoint.SubscriptionId}, resource group {endpoint.ResourceGroup}";

        // Check the routed sub-entity first (a missing parent also fails this).
        if (entity is not null)
        {
            var entityExists = await ResourceExistsAsync(entity, target.ApiVersion, endpoint.Name, tenant, cancellationToken);
            if (entityExists != false)
            {
                return null;   // exists or could not determine
            }

            // Distinguish a missing parent from a missing sub-entity for a precise explanation.
            var parentExists = await ResourceExistsAsync(parent, target.ApiVersion, endpoint.Name, tenant, cancellationToken);
            if (parentExists == false)
            {
                return $"the {parentNoun} '{endpoint.EndpointResourceName}' was not found in {location} — it may have been deleted, moved, or is offline, so IoT Hub cannot route to it until it is restored";
            }

            return $"the {entityKind} '{entityName}' was not found under the {parentNoun} '{endpoint.EndpointResourceName}' in {location} — it may have been deleted, so IoT Hub cannot route to it until it is restored";
        }

        // No identifiable sub-entity: check the parent only.
        var exists = await ResourceExistsAsync(parent, target.ApiVersion, endpoint.Name, tenant, cancellationToken);
        if (exists == false)
        {
            return $"the {parentNoun} '{endpoint.EndpointResourceName}' was not found in {location} — it may have been deleted, moved, or is offline, so IoT Hub cannot route to it until it is restored";
        }

        return null;
    }

    private async Task<ArmGetResult> GetArmAsync(
        string resourcePathOrUrl,
        string? apiVersion,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var token = await GetArmAccessTokenAsync(tenant, cancellationToken);
        var managementEndpoint = AzureService.CloudConfiguration.ArmEnvironment.Endpoint.ToString().TrimEnd('/');
        var url = Uri.TryCreate(resourcePathOrUrl, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri.ToString()
            : $"{managementEndpoint}{resourcePathOrUrl}";
        if (!string.IsNullOrEmpty(apiVersion))
        {
            url += $"{(url.Contains('?') ? '&' : '?')}api-version={apiVersion}";
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await AzureService.GetClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    // true = exists, false = not found, null = ARM responded but existence could not be determined.
    internal async Task<bool?> ResourceExistsAsync(
        ResourceIdentifier resourceId,
        string apiVersion,
        string endpointName,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var response = await GetArmAsync(resourceId.ToString(), apiVersion, tenant, cancellationToken);
        if (response.IsSuccess)
        {
            return true;
        }

        if (IsResourceNotFoundResponse(response.StatusCode, response.Content))
        {
            return false;
        }

        _logger.LogWarning(
            "Unable to verify existence of '{ResourceId}' for endpoint '{EndpointName}'. ARM returned status code {StatusCode}.",
            resourceId,
            endpointName,
            (int)response.StatusCode);
        return null;
    }

    internal static bool IsResourceNotFoundResponse(HttpStatusCode statusCode, string? responseContent)
    {
        if (statusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseContent);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && IsResourceNotFoundErrorCode(code.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<RoutingTargetConfigurationSignals?> QueryTargetConfigurationAsync(
        RoutingEndpointDetails endpoint,
        string? tenant,
        CancellationToken cancellationToken)
    {
        if (endpoint.EndpointType != "StorageContainer")
        {
            return null;
        }

        var (parent, _, _, _) = BuildTargetResourceIds(endpoint);
        if (parent is null)
        {
            return null;
        }

        try
        {
            var response = await GetArmAsync(parent.ToString(), s_storageTarget.ApiVersion, tenant, cancellationToken);
            if (!response.IsSuccess)
            {
                _logger.LogWarning(
                    "Unable to read target configuration for endpoint '{EndpointName}'. ARM returned status code {StatusCode}.",
                    endpoint.Name,
                    (int)response.StatusCode);
                return null;
            }

            return ParseStorageConfiguration(response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read target configuration for endpoint '{EndpointName}'.", endpoint.Name);
            return null;
        }
    }

    internal static RoutingTargetConfigurationSignals? ParseStorageConfiguration(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        var publicNetworkAccess = GetJsonString(properties, "publicNetworkAccess");
        string? defaultAction = null;
        string? bypass = null;
        if (properties.TryGetProperty("networkAcls", out var networkAcls))
        {
            defaultAction = GetJsonString(networkAcls, "defaultAction");
            bypass = GetJsonString(networkAcls, "bypass");
        }

        var warnings = new List<string>();
        if (string.Equals(publicNetworkAccess, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Public network access is disabled; verify that IoT Hub has an explicitly supported network path to the storage account.");
        }
        else if (string.Equals(defaultAction, "Deny", StringComparison.OrdinalIgnoreCase)
            && !ContainsCommaSeparatedValue(bypass, "AzureServices"))
        {
            warnings.Add("The storage firewall denies unmatched traffic and does not allow trusted Azure services; this may block IoT Hub routing.");
        }

        return new RoutingTargetConfigurationSignals(
            publicNetworkAccess,
            defaultAction,
            bypass,
            warnings.Count == 0 ? null : warnings);
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ContainsCommaSeparatedValue(string? values, string expected) =>
        values?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) == true;

    internal static (double? Average, double? Peak, IReadOnlyList<LatencyTrendPoint> Trend) SummarizeLatencyTrend(
        IEnumerable<LatencyTrendPoint> points)
    {
        var activePoints = points
            .Where(point => point.LatencyMsAvg > 0)
            .OrderBy(point => point.Timestamp)
            .ToList();
        if (activePoints.Count == 0)
        {
            return (null, null, []);
        }

        return (
            activePoints.Average(point => point.LatencyMsAvg),
            activePoints.Max(point => point.LatencyMsAvg),
            activePoints);
    }

    private async Task<TargetResourceHealth> QueryTargetResourceHealthAsync(
        RoutingEndpointDetails endpoint,
        ObservationWindow window,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        double? successfulRequests = null;
        double? serverErrors = null;
        double? userErrors = null;
        double? throttledRequests = null;
        IReadOnlyDictionary<string, double> errorBreakdown = EmptyErrorBreakdown;

        var resourceType = GetTargetDescriptor(endpoint.EndpointType)?.ResourceType;

        if (resourceType == null ||
            string.IsNullOrWhiteSpace(endpoint.SubscriptionId) ||
            string.IsNullOrWhiteSpace(endpoint.ResourceGroup) ||
            string.IsNullOrWhiteSpace(endpoint.EndpointResourceName))
        {
            return new TargetResourceHealth(successfulRequests, serverErrors, userErrors, throttledRequests, null, errorBreakdown);
        }

        try
        {
            var armClient = await CreateArmClientAsync(tenant, retryPolicy, cancellationToken: cancellationToken);
            (successfulRequests, serverErrors, userErrors, throttledRequests, errorBreakdown) =
                await QueryErrorSignalsAsync(endpoint, resourceType, window, armClient, cancellationToken);
        }
        catch (Exception ex) when (IsAuthorizationError(ex))
        {
            // Missing RBAC on the partner resource: keep results partial and surface an explicit per-endpoint error.
            _logger.LogWarning(ex, "Not authorized to read Azure Monitor metrics for '{ResourceName}' ({ResourceType}).", endpoint.EndpointResourceName, resourceType);
            var error = $"Authorization failed reading Azure Monitor metrics for {resourceType}/{endpoint.EndpointResourceName} in subscription {endpoint.SubscriptionId}. Grant the Monitoring Reader role on the target resource.";
            return new TargetResourceHealth(successfulRequests, serverErrors, userErrors, throttledRequests, error, errorBreakdown);
        }

        return new TargetResourceHealth(successfulRequests, serverErrors, userErrors, throttledRequests, null, errorBreakdown);
    }

    // Per-endpoint-type error/throughput signal query. Also returns a per-dimension error breakdown
    // (descriptive label -> count) so likelyFaultDetail can name the actual error source.
    private async Task<(double? Successful, double? ServerErrors, double? UserErrors, double? Throttled, IReadOnlyDictionary<string, double> ErrorBreakdown)> QueryErrorSignalsAsync(
        RoutingEndpointDetails endpoint,
        string resourceType,
        ObservationWindow window,
        ArmClient armClient,
        CancellationToken cancellationToken)
    {
        switch (endpoint.EndpointType)
        {
            case "EventHub":
                // Event Hub counts routed sends under SuccessfulRequests and reports quota-exceeded rejections separately.
                var eventHubMetrics = await QueryMetricsAsync(
                    endpoint, resourceType,
                    ["SuccessfulRequests", "ServerErrors", "UserErrors", "ThrottledRequests", "QuotaExceededErrors"],
                    "Total", window, armClient, cancellationToken);
                var ehServer = GetTotal(eventHubMetrics, "ServerErrors");
                var ehUser = GetTotal(eventHubMetrics, "UserErrors");
                var ehThrottled = GetTotal(eventHubMetrics, "ThrottledRequests");
                var ehQuota = GetTotal(eventHubMetrics, "QuotaExceededErrors");
                return (
                    GetTotal(eventHubMetrics, "SuccessfulRequests"),
                    ehServer,
                    ehUser,
                    Add(ehThrottled, ehQuota),
                    BuildNamedBreakdown(("ServerErrors", ehServer), ("UserErrors", ehUser), ("ThrottledRequests", ehThrottled), ("QuotaExceededErrors", ehQuota)));

            case "ServiceBusQueue":
            case "ServiceBusTopic":
                // Service Bus does not count IoT Hub routed sends under SuccessfulRequests; IncomingMessages reflects delivered messages.
                var serviceBusMetrics = await QueryMetricsAsync(
                    endpoint, resourceType,
                    ["IncomingMessages", "IncomingRequests", "ServerErrors", "UserErrors", "ThrottledRequests"],
                    "Total", window, armClient, cancellationToken);
                var sbServer = GetTotal(serviceBusMetrics, "ServerErrors");
                var sbUser = GetTotal(serviceBusMetrics, "UserErrors");
                var sbThrottled = GetTotal(serviceBusMetrics, "ThrottledRequests");
                return (
                    GetTotal(serviceBusMetrics, "IncomingMessages") ?? GetTotal(serviceBusMetrics, "IncomingRequests"),
                    sbServer,
                    sbUser,
                    sbThrottled,
                    BuildNamedBreakdown(("ServerErrors", sbServer), ("UserErrors", sbUser), ("ThrottledRequests", sbThrottled)));

            case "StorageContainer":
                // Storage exposes success/error/throttle only via the ResponseType dimension of Transactions.
                var storageSplit = await QuerySplitMetricAsync(
                    endpoint, resourceType, "Transactions", "ResponseType", "Total",
                    window, armClient, cancellationToken);
                var (storageSuccess, storageUser, storageServer, storageThrottled) = CategorizeStorageTransactions(storageSplit);
                return (storageSuccess, storageServer, storageUser, storageThrottled,
                    FilterErrorDimensions(storageSplit, key => !key.Contains("Success", StringComparison.OrdinalIgnoreCase)));

            case "CosmosDBSqlContainer":
                // Cosmos exposes success/error/throttle only via the StatusCode dimension of TotalRequests.
                var cosmosSplit = await QuerySplitMetricAsync(
                    endpoint, resourceType, "TotalRequests", "StatusCode", "Count",
                    window, armClient, cancellationToken);
                var (cosmosSuccess, cosmosUser, cosmosServer, cosmosThrottled) = CategorizeStatusCodes(cosmosSplit);
                var cosmosBreakdown = FilterErrorDimensions(cosmosSplit, key => !key.StartsWith('2'), key => $"HTTP {key}");

                // Additional signal: peak normalized RU consumption confirms RU-exhaustion as the throttling
                // root cause. Surface it alongside the status-code breakdown when it is running hot.
                var cosmosRu = await QueryMetricsAsync(
                    endpoint, resourceType, ["NormalizedRUConsumption"], "Maximum",
                    window, armClient, cancellationToken);
                var cosmosRuMax = GetMaximum(cosmosRu, "NormalizedRUConsumption");
                if (cosmosRuMax >= 90 && cosmosBreakdown.Count > 0)
                {
                    var withRu = new Dictionary<string, double>(cosmosBreakdown, StringComparer.OrdinalIgnoreCase)
                    {
                        ["peak RU consumption %"] = Math.Round(cosmosRuMax.Value)
                    };
                    cosmosBreakdown = withRu;
                }

                return (cosmosSuccess, cosmosServer, cosmosUser, cosmosThrottled, cosmosBreakdown);

            default:
                return (null, null, null, null, EmptyErrorBreakdown);
        }
    }

    private static readonly IReadOnlyDictionary<string, double> EmptyErrorBreakdown = new Dictionary<string, double>();

    // Keeps only non-zero entries whose dimension value is an error (per the predicate), optionally relabeled.
    private static IReadOnlyDictionary<string, double> FilterErrorDimensions(
        IReadOnlyDictionary<string, double> byDimension,
        Func<string, bool> isError,
        Func<string, string>? relabel = null)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in byDimension)
        {
            if (value > 0 && isError(key))
            {
                result[relabel?.Invoke(key) ?? key] = value;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> BuildNamedBreakdown(params (string Label, double? Count)[] entries)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, count) in entries)
        {
            if ((count ?? 0) > 0)
            {
                result[label] = count!.Value;
            }
        }

        return result;
    }

    private static bool IsAuthorizationError(Exception ex) =>
        (ex is Azure.RequestFailedException rfe && (rfe.Status == 401 || rfe.Status == 403))
        || ex is Azure.Identity.AuthenticationFailedException
        || ex.Message.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase);

    private static bool IsResourceNotFoundErrorCode(string? errorCode) =>
        errorCode is not null
        && (errorCode.Equals("ResourceNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ParentResourceNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("EntityNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("MessagingEntityNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ContainerNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("NotFound", StringComparison.OrdinalIgnoreCase));

    internal async Task<Dictionary<string, EndpointHealthData>> GetRoutingEndpointsHealthAsync(
        ResourceIdentifier hubResourceId,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, EndpointHealthData>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? nextLink = $"{hubResourceId}/routingEndpointsHealth";
            string? apiVersion = "2023-06-30";
            while (!string.IsNullOrEmpty(nextLink))
            {
                var response = await GetArmAsync(nextLink, apiVersion, tenant, cancellationToken);
                if (!response.IsSuccess)
                {
                    throw new HttpRequestException(
                        $"ARM returned status code {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }

                var result = JsonSerializer.Deserialize(
                    response.Content,
                    IoTHubJsonContext.Default.EndpointHealthDataListResult);

                foreach (var data in result?.Value ?? [])
                {
                    if (!string.IsNullOrEmpty(data.EndpointId))
                    {
                        map[data.EndpointId] = data;
                    }
                }

                nextLink = result?.NextLink;
                apiVersion = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to query routing endpoint health for IoT Hub '{HubResourceId}'.", hubResourceId);
        }

        return map;
    }

    private static double? ComputeSendToSuccessLatency(DateTimeOffset? lastSendAttempt, DateTimeOffset? lastSuccessfulSend)
    {
        if (lastSendAttempt is null || lastSuccessfulSend is null)
        {
            return null;
        }

        var lag = (lastSendAttempt.Value - lastSuccessfulSend.Value).TotalMilliseconds;
        return lag < 0 ? 0 : lag;
    }

    private static bool IsFailureStatus(string? status) =>
        status is not null &&
        (status.Equals("unhealthy", StringComparison.OrdinalIgnoreCase)
            || status.Equals("dead", StringComparison.OrdinalIgnoreCase)
            || status.Equals("degraded", StringComparison.OrdinalIgnoreCase));

    internal static bool HasErrorInWindow(
        string? lastKnownError,
        DateTimeOffset? lastKnownErrorTime,
        ObservationWindow window)
    {
        if (string.IsNullOrEmpty(lastKnownError) || lastKnownErrorTime is null)
        {
            return false;
        }

        return window.Contains(lastKnownErrorTime);
    }

    private static bool TryParseRfc1123(string? value, out DateTimeOffset result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static DateTimeOffset? ParseRfc1123OrNull(string? value) =>
        TryParseRfc1123(value, out var result) ? result : null;

    private static Dictionary<string, string> BuildEndpointIdToNameMap(RoutingEndpoints? endpoints)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (endpoints == null)
        {
            return map;
        }

        void Add(string? id, string? name)
        {
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                map[id] = name;
            }
        }

        foreach (var e in EnumerateEndpoints(endpoints))
            Add(e.Id, e.Name);

        return map;
    }

    // Queries the hub's RoutingDeliveries + RoutingDeliveryLatency ONCE for every endpoint (split by the
    // EndpointName dimension via `EndpointName eq '*'`), instead of two queries per endpoint. Returns the
    // per-endpoint signals keyed by endpoint name.
    private async Task<IReadOnlyDictionary<string, RoutingDeliverySignals>> QueryAllRoutingDeliveriesAsync(
        ResourceIdentifier hubResourceId,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        const string metricNamespace = "Microsoft.Devices/IotHubs";
        var armClientTask = CreateArmClientAsync(tenant, retryPolicy, cancellationToken: cancellationToken);

        // endpointName -> (success, failure) routing-event counts.
        var deliveries = new Dictionary<string, (double? Success, double? Failure)>(StringComparer.OrdinalIgnoreCase);
        // endpointName -> bucket -> average latency.
        var latencyByEndpoint = new Dictionary<string, Dictionary<DateTimeOffset, double>>(StringComparer.OrdinalIgnoreCase);

        static string? Dimension(MonitorTimeSeriesElement series, string name) => series.Metadatavalues
            .FirstOrDefault(v => string.Equals(v.Name?.Value, name, StringComparison.OrdinalIgnoreCase))?.Value;

        // RoutingDeliveries (Total, split by Result) gives the routing-event counts; RoutingDeliveryLatency
        // (Average) gives the latency series. They are independent metrics on the hub; query concurrently.
        async Task QueryDeliveriesAsync()
        {
            try
            {
                var armClient = await armClientTask;
                var options = new ArmResourceGetMonitorMetricsOptions
                {
                    Metricnames = "RoutingDeliveries",
                    Metricnamespace = metricNamespace,
                    Timespan = window.Timespan,
                    Interval = interval,
                    Aggregation = "Total",
                    Filter = "EndpointName eq '*' and Result eq '*'"
                };

                var metrics = armClient.GetMonitorMetricsAsync(hubResourceId, options, cancellationToken);
                await foreach (var metric in metrics.WithCancellation(cancellationToken))
                {
                    foreach (var series in metric.Timeseries)
                    {
                        var endpointName = Dimension(series, "EndpointName");
                        if (string.IsNullOrEmpty(endpointName))
                        {
                            continue;
                        }

                        var result = Dimension(series, "Result");
                        var isSuccess = string.Equals(result, "success", StringComparison.OrdinalIgnoreCase);
                        if (!isSuccess && string.IsNullOrEmpty(result))
                        {
                            continue;
                        }

                        var sum = series.Data.Sum(point => point.Total ?? point.Count ?? 0);
                        var current = deliveries.GetValueOrDefault(endpointName);
                        deliveries[endpointName] = isSuccess
                            ? ((current.Success ?? 0) + sum, current.Failure)
                            : (current.Success, (current.Failure ?? 0) + sum);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to query RoutingDeliveries for IoT Hub '{HubResourceId}'.", hubResourceId);
            }
        }

        async Task QueryLatencyAsync()
        {
            try
            {
                var armClient = await armClientTask;
                var options = new ArmResourceGetMonitorMetricsOptions
                {
                    Metricnames = "RoutingDeliveryLatency",
                    Metricnamespace = metricNamespace,
                    Timespan = window.Timespan,
                    Interval = interval,
                    Aggregation = "Average",
                    Filter = "EndpointName eq '*'"
                };

                var metrics = armClient.GetMonitorMetricsAsync(hubResourceId, options, cancellationToken);
                await foreach (var metric in metrics.WithCancellation(cancellationToken))
                {
                    foreach (var series in metric.Timeseries)
                    {
                        var endpointName = Dimension(series, "EndpointName");
                        if (string.IsNullOrEmpty(endpointName))
                        {
                            continue;
                        }

                        if (!latencyByEndpoint.TryGetValue(endpointName, out var byHour))
                        {
                            byHour = [];
                            latencyByEndpoint[endpointName] = byHour;
                        }

                        foreach (var point in series.Data)
                        {
                            if (point.Average.HasValue)
                            {
                                byHour[point.TimeStamp] = point.Average.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to query RoutingDeliveryLatency for IoT Hub '{HubResourceId}'.", hubResourceId);
            }
        }

        await Task.WhenAll(QueryDeliveriesAsync(), QueryLatencyAsync());

        var result = new Dictionary<string, RoutingDeliverySignals>(StringComparer.OrdinalIgnoreCase);
        var endpointNames = new HashSet<string>(deliveries.Keys, StringComparer.OrdinalIgnoreCase);
        endpointNames.UnionWith(latencyByEndpoint.Keys);
        foreach (var name in endpointNames)
        {
            var (success, failure) = deliveries.GetValueOrDefault(name);
            double? latency = null;
            double? peakLatency = null;
            IReadOnlyList<LatencyTrendPoint> trend = [];
            if (latencyByEndpoint.TryGetValue(name, out var byHour) && byHour.Count > 0)
            {
                var summary = SummarizeLatencyTrend(
                    byHour.Select(entry => new LatencyTrendPoint(entry.Key, entry.Value)));
                latency = summary.Average;
                peakLatency = summary.Peak;
                trend = summary.Trend;
            }

            result[name] = new RoutingDeliverySignals(success, failure, latency, peakLatency, trend);
        }

        return result;
    }

    private async Task<Dictionary<string, MetricValue>> QueryMetricsAsync(
        RoutingEndpointDetails endpoint,
        string resourceType,
        string[] metricNames,
        string aggregation,
        ObservationWindow window,
        ArmClient armClient,
        CancellationToken cancellationToken)
    {
        if (metricNames.Length == 0)
        {
            return [];
        }

        try
        {
            return await QueryMetricsBatchAsync(
                endpoint, resourceType, metricNames, aggregation, window, armClient, cancellationToken);
        }
        catch (Exception ex) when (metricNames.Length > 1)
        {
            // A single unsupported metric fails the whole batch; retry each metric alone (concurrently)
            // so the rest still resolve without paying a per-metric round-trip in series.
            _logger.LogWarning(ex, "Batched metric query failed for '{ResourceName}'; retrying metrics individually.", endpoint.EndpointResourceName);
            var singleQueries = metricNames.Select(async metricName =>
            {
                try
                {
                    return await QueryMetricsBatchAsync(
                        endpoint, resourceType, [metricName], aggregation, window, armClient, cancellationToken);
                }
                catch (Exception singleEx)
                {
                    if (IsAuthorizationError(singleEx))
                    {
                        throw;
                    }

                    _logger.LogWarning(singleEx, "Metric '{MetricName}' is unavailable for '{ResourceName}'.", metricName, endpoint.EndpointResourceName);
                    return new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase);
                }
            });

            var results = await Task.WhenAll(singleQueries);
            var values = new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
            {
                foreach (var entry in result)
                {
                    values[entry.Key] = entry.Value;
                }
            }

            return values;
        }
    }

    private async Task<Dictionary<string, MetricValue>> QueryMetricsBatchAsync(
        RoutingEndpointDetails endpoint,
        string resourceType,
        string[] metricNames,
        string aggregation,
        ObservationWindow window,
        ArmClient armClient,
        CancellationToken cancellationToken)
    {
        var resourceId = BuildResourceId(
            endpoint.SubscriptionId!,
            endpoint.ResourceGroup!,
            resourceType,
            endpoint.EndpointResourceName!);
        var options = new ArmResourceGetMonitorMetricsOptions
        {
            Metricnames = string.Join(",", metricNames),
            Metricnamespace = resourceType,
            Timespan = window.Timespan,
            Interval = TimeSpan.FromHours(1),
            Aggregation = aggregation
        };

        var values = new Dictionary<string, MetricValue>(StringComparer.OrdinalIgnoreCase);
        var metrics = armClient.GetMonitorMetricsAsync(resourceId, options, cancellationToken);
        await foreach (var metric in metrics.WithCancellation(cancellationToken))
        {
            var data = metric.Timeseries.SelectMany(series => series.Data).ToList();
            var totals = data.Where(point => point.Total.HasValue).Select(point => point.Total!.Value).ToArray();
            var counts = data.Where(point => point.Count.HasValue).Select(point => point.Count!.Value).ToArray();
            var averages = data.Where(point => point.Average.HasValue).Select(point => point.Average!.Value).ToArray();
            var maximums = data.Where(point => point.Maximum.HasValue).Select(point => point.Maximum!.Value).ToArray();
            // Prefer Total; fall back to Count for count-primary metrics (e.g. Cosmos TotalRequests).
            double? sum = totals.Length > 0 ? totals.Sum() : counts.Length > 0 ? counts.Sum() : null;
            values[metric.Name?.Value ?? string.Empty] = new(
                sum,
                averages.Length == 0 ? null : averages.Average(),
                maximums.Length == 0 ? null : maximums.Max());
        }

        return values;
    }

    private async Task<Dictionary<string, double>> QuerySplitMetricAsync(
        RoutingEndpointDetails endpoint,
        string resourceType,
        string metricName,
        string dimension,
        string aggregation,
        ObservationWindow window,
        ArmClient armClient,
        CancellationToken cancellationToken)
    {
        var resourceId = BuildResourceId(
            endpoint.SubscriptionId!,
            endpoint.ResourceGroup!,
            resourceType,
            endpoint.EndpointResourceName!);
        var options = new ArmResourceGetMonitorMetricsOptions
        {
            Metricnames = metricName,
            Metricnamespace = resourceType,
            Timespan = window.Timespan,
            Interval = TimeSpan.FromHours(1),
            Aggregation = aggregation,
            Filter = $"{dimension} eq '*'"
        };

        var byDimension = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var metrics = armClient.GetMonitorMetricsAsync(resourceId, options, cancellationToken);
            await foreach (var metric in metrics.WithCancellation(cancellationToken))
            {
                foreach (var series in metric.Timeseries)
                {
                    var dimensionValue = series.Metadatavalues
                        .FirstOrDefault(v => string.Equals(v.Name?.Value, dimension, StringComparison.OrdinalIgnoreCase))?.Value;
                    if (string.IsNullOrEmpty(dimensionValue))
                    {
                        continue;
                    }

                    var sum = series.Data.Sum(point => point.Total ?? point.Count ?? 0);
                    byDimension[dimensionValue] = byDimension.GetValueOrDefault(dimensionValue) + sum;
                }
            }
        }
        catch (Exception ex)
        {
            if (IsAuthorizationError(ex))
            {
                throw;
            }

            // Degrade gracefully so a failed split query doesn't discard other health signals for the endpoint.
            _logger.LogWarning(ex, "Split metric '{MetricName}' is unavailable for '{ResourceName}'.", metricName, endpoint.EndpointResourceName);
        }

        return byDimension;
    }

    internal static (double? Successful, double? UserErrors, double? ServerErrors, double? Throttled) CategorizeStorageTransactions(
        IReadOnlyDictionary<string, double> byResponseType)
    {
        if (byResponseType.Count == 0)
        {
            return (null, null, null, null);
        }

        double success = 0, user = 0, server = 0, throttled = 0;
        foreach (var (responseType, value) in byResponseType)
        {
            if (responseType.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                success += value;
            }
            else if (responseType.Contains("Throttl", StringComparison.OrdinalIgnoreCase) || responseType.Contains("ServerBusy", StringComparison.OrdinalIgnoreCase))
            {
                throttled += value;
            }
            // Client-side (4xx) errors: authorization/authentication failures (e.g. AuthorizationError from a
            // missing RBAC role) and the Client* family, including SAS/Anonymous-prefixed variants
            // (SASClientOtherError, AnonymousAuthorizationError, ...). These do not all start with "Client",
            // so match on substrings - otherwise a 403/401 would be misattributed as a server (5xx) error.
            else if (responseType.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || responseType.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                || responseType.Contains("Client", StringComparison.OrdinalIgnoreCase))
            {
                user += value;
            }
            else
            {
                server += value;
            }
        }

        return (success, user, server, throttled);
    }

    private static (double? Successful, double? UserErrors, double? ServerErrors, double? Throttled) CategorizeStatusCodes(
        IReadOnlyDictionary<string, double> byStatusCode)
    {
        if (byStatusCode.Count == 0)
        {
            return (null, null, null, null);
        }

        double success = 0, user = 0, server = 0, throttled = 0;
        foreach (var (statusCode, value) in byStatusCode)
        {
            if (statusCode == "429")
            {
                throttled += value;
            }
            else if (statusCode.StartsWith('2'))
            {
                success += value;
            }
            else if (statusCode.StartsWith('4'))
            {
                user += value;
            }
            else if (statusCode.StartsWith('5'))
            {
                server += value;
            }
        }

        return (success, user, server, throttled);
    }

    private static double? Add(double? a, double? b) =>
        a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static double? GetTotal(IReadOnlyDictionary<string, MetricValue> metrics, string name) =>
        metrics.TryGetValue(name, out var value) ? value.Total : null;

    private static double? GetMaximum(IReadOnlyDictionary<string, MetricValue> metrics, string name) =>
        metrics.TryGetValue(name, out var value) ? value.Maximum : null;

    private static string ToIsoString(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)
            : value.ToString("O", CultureInfo.InvariantCulture);

    private static ResourceIdentifier BuildResourceId(string subscription, string resourceGroup, string resourceType, string resourceName) =>
        new($"/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/{resourceType}/{resourceName}");

    private sealed record MetricValue(double? Total, double? Average, double? Maximum = null);

    private static string? GetEndpointResourceName(
        string? id,
        string? endpointUri,
        string? fallback,
        bool preferEndpointUri = false)
    {
        if (preferEndpointUri && TryGetEndpointHostName(endpointUri, out var preferredName))
        {
            return preferredName;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return new ResourceIdentifier(id).Name;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
            }
        }

        if (TryGetEndpointHostName(endpointUri, out var endpointName))
        {
            return endpointName;
        }

        return fallback;
    }

    private static bool TryGetEndpointHostName(string? endpointUri, out string? endpointName)
    {
        endpointName = null;

        if (!Uri.TryCreate(endpointUri, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        endpointName = uri.Host.Split('.')[0];
        return !string.IsNullOrWhiteSpace(endpointName);
    }
}
