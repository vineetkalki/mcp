// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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
    private readonly ILogger<IoTHubService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IoTHubDescription> GetIoTHub(
        string hubName,
        string resourceGroup,
        string subscription,
        string? tenant = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(hubName, resourceGroup, subscription);

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
            _logger.LogError(
                ex,
                "Error retrieving IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                hubName,
                resourceGroup,
                subscription);
            throw;
        }
    }

    public async Task<List<RoutingEndpointHealthSnapshot>> GetRoutingEndpointHealth(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(hubName, resourceGroup, subscription);

        try
        {
            var hub = await GetIoTHubGenericResourceAsync(
                hubName,
                resourceGroup,
                subscription,
                tenant,
                retryPolicy,
                cancellationToken);
            var properties = hub.Properties?.ToObjectFromJson(IoTHubJsonContext.Default.IoTHubProperties);
            var endpoints = FilterEndpoints(
                ConvertToRoutingEndpointDetailsList(properties?.Routing?.Endpoints),
                endpointName,
                hubName);
            var endpointNames = endpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.EndpointId))
                .ToDictionary(
                    endpoint => endpoint.EndpointId!,
                    endpoint => endpoint.Name,
                    StringComparer.OrdinalIgnoreCase);
            var healthRecords = await GetRoutingEndpointsHealthAsync(
                hub.Id,
                tenant,
                cancellationToken);

            return
            [
                .. healthRecords
                    .Where(record =>
                        !string.IsNullOrWhiteSpace(record.EndpointId) &&
                        endpointNames.ContainsKey(record.EndpointId))
                    .Select(record => new RoutingEndpointHealthSnapshot(
                        EndpointId: record.EndpointId!,
                        EndpointName: endpointNames[record.EndpointId!],
                        HealthStatus: record.HealthStatus,
                        LastKnownError: record.LastKnownError,
                        LastKnownErrorTime: record.LastKnownErrorTime,
                        LastSuccessfulSendAttemptTime: record.LastSuccessfulSendAttemptTime,
                        LastSendAttemptTime: record.LastSendAttemptTime))
            ];
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving routing endpoint health for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                hubName,
                resourceGroup,
                subscription);
            throw;
        }
    }

    public async Task<RoutingEndpointDiagnostics> GetRoutingEndpointDiagnostics(
        string hubName,
        string resourceGroup,
        string subscription,
        string? endpointName = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        string? interval = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(hubName, resourceGroup, subscription);
        var resolvedWindow = IoTHubValidation.ResolveDiagnosticsWindow(startTime, endTime, interval);
        var window = new ObservationWindow(resolvedWindow.StartTime, resolvedWindow.EndTime);

        try
        {
            var hub = await GetIoTHubGenericResourceAsync(
                hubName,
                resourceGroup,
                subscription,
                tenant,
                retryPolicy,
                cancellationToken);
            var properties = hub.Properties?.ToObjectFromJson(IoTHubJsonContext.Default.IoTHubProperties);
            var endpoints = FilterEndpoints(
                ConvertToRoutingEndpointDetailsList(properties?.Routing?.Endpoints),
                endpointName,
                hubName);
            var hubEvidence = await QueryHubEvidenceAsync(
                hub.Id,
                endpoints,
                window,
                resolvedWindow.Interval,
                tenant,
                retryPolicy,
                cancellationToken);
            var targetEvidence = new ConcurrentDictionary<string, Lazy<Task<RoutingTargetEmitted>>>(
                StringComparer.OrdinalIgnoreCase);

            var endpointResults = await Task.WhenAll(endpoints.Select(endpoint =>
                BuildEndpointDiagnosticsAsync(
                    endpoint,
                    hubEvidence.GetValueOrDefault(endpoint.Name) ?? CreateEmptyHubEvidence(),
                    window,
                    resolvedWindow.Interval,
                    tenant,
                    retryPolicy,
                    targetEvidence,
                    cancellationToken)));

            return new RoutingEndpointDiagnostics(
                new RoutingObservationWindow(
                    ToIsoString(window.StartTime),
                    ToIsoString(window.EndTime),
                    XmlConvert.ToString(resolvedWindow.Interval)),
                [.. endpointResults]);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving routing endpoint diagnostics for IoT Hub '{HubName}' in resource group '{ResourceGroup}' and subscription '{Subscription}'.",
                hubName,
                resourceGroup,
                subscription);
            throw;
        }
    }

    private async Task<RoutingEndpointDiagnostic> BuildEndpointDiagnosticsAsync(
        RoutingEndpointDetails endpoint,
        RoutingHubEmitted hubEvidence,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        ConcurrentDictionary<string, Lazy<Task<RoutingTargetEmitted>>> targetEvidence,
        CancellationToken cancellationToken)
    {
        var resolution = ResolveTarget(endpoint);
        if (resolution is null)
        {
            var resolutionStatus = GetTargetDescriptor(endpoint.EndpointType) is null
                ? "unsupported"
                : "unresolved";
            return CreateEndpointDiagnostic(
                endpoint,
                new RoutingTargetInfo(resolutionStatus, ExistenceStatus: "notChecked"),
                hubEvidence,
                CreateEmptyTargetEvidence(resolutionStatus));
        }

        var existenceTask = GetTargetExistenceAsync(
            resolution.RoutedResourceId,
            resolution.Descriptor.ApiVersion,
            endpoint.Name,
            tenant,
            cancellationToken);
        var evidenceTask = targetEvidence.GetOrAdd(
            resolution.ParentResourceId.ToString(),
            _ => new Lazy<Task<RoutingTargetEmitted>>(
                () => QueryTargetEvidenceAsync(
                    resolution.ParentResourceId,
                    resolution.Descriptor,
                    window,
                    interval,
                    tenant,
                    retryPolicy,
                    cancellationToken))).Value;

        await Task.WhenAll(existenceTask, evidenceTask);
        var existence = await existenceTask;
        var targetMetrics = await evidenceTask;
        if (existence.Error is not null)
        {
            var errors = new List<RoutingDiagnosticError>(targetMetrics.Errors)
            {
                existence.Error
            };
            targetMetrics = targetMetrics with
            {
                QueryStatus = targetMetrics.QueryStatus == "queried" ? "partial" : targetMetrics.QueryStatus,
                Errors = errors
            };
        }

        var target = new RoutingTargetInfo(
            "resolved",
            resolution.ParentResourceId.ToString(),
            resolution.Descriptor.ResourceType,
            resolution.RoutedResourceId.ToString(),
            existence.Status,
            ToIsoString(existence.ObservedAt));
        return CreateEndpointDiagnostic(endpoint, target, hubEvidence, targetMetrics);
    }

    private static RoutingEndpointDiagnostic CreateEndpointDiagnostic(
        RoutingEndpointDetails endpoint,
        RoutingTargetInfo target,
        RoutingHubEmitted hubEvidence,
        RoutingTargetEmitted targetEvidence) => new(
            EndpointId: endpoint.EndpointId ?? string.Empty,
            Name: endpoint.Name,
            EndpointType: endpoint.EndpointType,
            AuthenticationType: endpoint.AuthenticationType,
            EndpointUri: endpoint.EndpointUri,
            SubscriptionId: endpoint.SubscriptionId,
            ResourceGroup: endpoint.ResourceGroup,
            EndpointResourceName: endpoint.EndpointResourceName,
            EntityPath: endpoint.EntityPath,
            ContainerName: endpoint.ContainerName,
            DatabaseName: endpoint.DatabaseName,
            BatchFrequencyInSeconds: endpoint.BatchFrequencyInSeconds,
            Target: target,
            HubEmitted: hubEvidence,
            TargetEmitted: targetEvidence);

    private async Task<Dictionary<string, RoutingHubEmitted>> QueryHubEvidenceAsync(
        ResourceIdentifier hubResourceId,
        IReadOnlyList<RoutingEndpointDetails> endpoints,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        const string metricNamespace = "Microsoft.Devices/IotHubs";
        var builders = endpoints.ToDictionary(
            endpoint => endpoint.Name,
            _ => new RoutingMetricEvidenceBuilder(metricNamespace),
            StringComparer.OrdinalIgnoreCase);
        var errors = endpoints.ToDictionary(
            endpoint => endpoint.Name,
            _ => new List<RoutingDiagnosticError>(),
            StringComparer.OrdinalIgnoreCase);
        var armClient = await CreateArmClientAsync(
            tenant,
            retryPolicy,
            cancellationToken: cancellationToken);

        try
        {
            var options = BuildMonitorMetricsOptions(
                "RoutingDeliveries",
                metricNamespace,
                window,
                interval,
                "Total",
                "EndpointName eq '*' and Result eq '*' and FailureReasonCategory eq '*'");
            var metrics = armClient.GetMonitorMetricsAsync(hubResourceId, options, cancellationToken);
            await foreach (var metric in metrics.WithCancellation(cancellationToken))
            {
                foreach (var series in metric.Timeseries)
                {
                    var endpointName = GetDimensionValue(series, "EndpointName");
                    if (string.IsNullOrWhiteSpace(endpointName) ||
                        !builders.TryGetValue(endpointName, out var builder))
                    {
                        continue;
                    }

                    var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
                    AddDimensionIfPresent(series, dimensions, "Result");
                    AddDimensionIfPresent(series, dimensions, "FailureReasonCategory");
                    try
                    {
                        builder.AddCountSeries(
                            "RoutingDeliveries",
                            "Total",
                            dimensions,
                            series.Data.Select(point => (point.TimeStamp, point.Total ?? point.Count)));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        errors[endpointName].Add(CreateDiagnosticError(
                            ex,
                            "hubAzureMonitor",
                            "RoutingDeliveries",
                            hubResourceId.ToString(),
                            "Reader",
                            "IoT Hub routing delivery counts could not be represented."));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to query RoutingDeliveries for IoT Hub '{HubResourceId}'.",
                hubResourceId);
            AddSharedError(
                errors,
                CreateDiagnosticError(
                    ex,
                    "hubAzureMonitor",
                    "RoutingDeliveries",
                    hubResourceId.ToString(),
                    "Reader",
                    "IoT Hub routing delivery metrics could not be read."));
        }

        try
        {
            var options = BuildMonitorMetricsOptions(
                "RoutingDeliveryLatency",
                metricNamespace,
                window,
                interval,
                "Average",
                "EndpointName eq '*'");
            var metrics = armClient.GetMonitorMetricsAsync(hubResourceId, options, cancellationToken);
            await foreach (var metric in metrics.WithCancellation(cancellationToken))
            {
                foreach (var series in metric.Timeseries)
                {
                    var endpointName = GetDimensionValue(series, "EndpointName");
                    if (string.IsNullOrWhiteSpace(endpointName) ||
                        !builders.TryGetValue(endpointName, out var builder))
                    {
                        continue;
                    }

                    try
                    {
                        builder.AddAverageSeries(
                            "RoutingDeliveryLatency",
                            "Milliseconds",
                            EmptyDimensions,
                            series.Data.Select(point => (point.TimeStamp, point.Average)));
                    }
                    catch (InvalidDataException ex)
                    {
                        errors[endpointName].Add(CreateDiagnosticError(
                            ex,
                            "hubAzureMonitor",
                            "RoutingDeliveryLatency",
                            hubResourceId.ToString(),
                            "Reader",
                            "IoT Hub routing delivery latency could not be represented."));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to query RoutingDeliveryLatency for IoT Hub '{HubResourceId}'.",
                hubResourceId);
            AddSharedError(
                errors,
                CreateDiagnosticError(
                    ex,
                    "hubAzureMonitor",
                    "RoutingDeliveryLatency",
                    hubResourceId.ToString(),
                    "Reader",
                    "IoT Hub routing delivery latency metrics could not be read."));
        }

        return endpoints.ToDictionary(
            endpoint => endpoint.Name,
            endpoint => new RoutingHubEmitted(
                builders[endpoint.Name].Build(),
                errors[endpoint.Name]),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<RoutingTargetEmitted> QueryTargetEvidenceAsync(
        ResourceIdentifier resourceId,
        RoutingTargetDescriptor descriptor,
        ObservationWindow window,
        TimeSpan interval,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        var builder = new RoutingMetricEvidenceBuilder(descriptor.ResourceType);
        var errors = new List<RoutingDiagnosticError>();
        var armClient = await CreateArmClientAsync(
            tenant,
            retryPolicy,
            cancellationToken: cancellationToken);

        switch (descriptor.EndpointType)
        {
            case "EventHub":
                await QueryMetricSetWithFallbackAsync(
                    resourceId,
                    descriptor.ResourceType,
                    ["SuccessfulRequests", "ServerErrors", "UserErrors", "ThrottledRequests", "QuotaExceededErrors"],
                    "Total",
                    "Count",
                    null,
                    window,
                    interval,
                    armClient,
                    builder,
                    errors,
                    cancellationToken);
                break;
            case "ServiceBusQueue":
            case "ServiceBusTopic":
                await QueryMetricSetWithFallbackAsync(
                    resourceId,
                    descriptor.ResourceType,
                    ["IncomingMessages", "IncomingRequests", "ServerErrors", "UserErrors", "ThrottledRequests"],
                    "Total",
                    "Count",
                    null,
                    window,
                    interval,
                    armClient,
                    builder,
                    errors,
                    cancellationToken);
                break;
            case "StorageContainer":
                await QueryMetricSetWithFallbackAsync(
                    resourceId,
                    descriptor.ResourceType,
                    ["Transactions"],
                    "Total",
                    "Count",
                    "ResponseType eq '*'",
                    window,
                    interval,
                    armClient,
                    builder,
                    errors,
                    cancellationToken);
                break;
            case "CosmosDBSqlContainer":
                await QueryMetricSetWithFallbackAsync(
                    resourceId,
                    descriptor.ResourceType,
                    ["TotalRequests"],
                    "Count",
                    "Count",
                    "StatusCode eq '*'",
                    window,
                    interval,
                    armClient,
                    builder,
                    errors,
                    cancellationToken);
                await QueryMetricSetWithFallbackAsync(
                    resourceId,
                    descriptor.ResourceType,
                    ["NormalizedRUConsumption"],
                    "Maximum",
                    "Percent",
                    null,
                    window,
                    interval,
                    armClient,
                    builder,
                    errors,
                    cancellationToken);
                break;
        }

        var evidence = builder.Build();
        return new RoutingTargetEmitted(
            GetQueryStatus(builder.HasValues, errors),
            "parentResource",
            descriptor.ResourceType,
            evidence.WindowAggregates,
            evidence.Buckets,
            errors);
    }

    private async Task QueryMetricSetWithFallbackAsync(
        ResourceIdentifier resourceId,
        string resourceType,
        string[] metricNames,
        string aggregation,
        string unit,
        string? filter,
        ObservationWindow window,
        TimeSpan interval,
        ArmClient armClient,
        RoutingMetricEvidenceBuilder destination,
        List<RoutingDiagnosticError> errors,
        CancellationToken cancellationToken)
    {
        var batch = new RoutingMetricEvidenceBuilder(resourceType);
        try
        {
            await QueryMetricSetCoreAsync(
                resourceId,
                resourceType,
                metricNames,
                aggregation,
                unit,
                filter,
                window,
                interval,
                armClient,
                batch,
                cancellationToken);
            destination.Merge(batch.Build());
            return;
        }
        catch (Exception ex) when (
            metricNames.Length > 1 &&
            !IsAuthorizationError(ex) &&
            !IsNotFoundException(ex))
        {
            _logger.LogWarning(
                ex,
                "Batched target metric query failed for '{ResourceId}'; retrying metrics individually.",
                resourceId);
        }
        catch (Exception ex)
        {
            errors.Add(CreateTargetMetricError(ex, resourceId, string.Join(",", metricNames)));
            return;
        }

        foreach (var metricName in metricNames)
        {
            var singleMetric = new RoutingMetricEvidenceBuilder(resourceType);
            try
            {
                await QueryMetricSetCoreAsync(
                    resourceId,
                    resourceType,
                    [metricName],
                    aggregation,
                    unit,
                    filter,
                    window,
                    interval,
                    armClient,
                    singleMetric,
                    cancellationToken);
                destination.Merge(singleMetric.Build());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Target metric '{MetricName}' is unavailable for '{ResourceId}'.",
                    metricName,
                    resourceId);
                errors.Add(CreateTargetMetricError(ex, resourceId, metricName));
            }
        }
    }

    private static async Task QueryMetricSetCoreAsync(
        ResourceIdentifier resourceId,
        string resourceType,
        string[] metricNames,
        string aggregation,
        string unit,
        string? filter,
        ObservationWindow window,
        TimeSpan interval,
        ArmClient armClient,
        RoutingMetricEvidenceBuilder builder,
        CancellationToken cancellationToken)
    {
        var options = BuildMonitorMetricsOptions(
            string.Join(",", metricNames),
            resourceType,
            window,
            interval,
            aggregation,
            filter);
        var metrics = armClient.GetMonitorMetricsAsync(resourceId, options, cancellationToken);
        await foreach (var metric in metrics.WithCancellation(cancellationToken))
        {
            var metricName = metric.Name?.Value ?? metricNames[0];
            foreach (var series in metric.Timeseries)
            {
                var dimensions = GetDimensions(series);
                switch (aggregation)
                {
                    case "Total":
                        builder.AddCountSeries(
                            metricName,
                            aggregation,
                            dimensions,
                            series.Data.Select(point => (point.TimeStamp, point.Total ?? point.Count)));
                        break;
                    case "Count":
                        builder.AddCountSeries(
                            metricName,
                            aggregation,
                            dimensions,
                            series.Data.Select(point => (point.TimeStamp, point.Count ?? point.Total)));
                        break;
                    case "Maximum":
                        builder.AddMaximumSeries(
                            metricName,
                            unit,
                            dimensions,
                            series.Data.Select(point => (point.TimeStamp, point.Maximum)));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported target metric aggregation '{aggregation}'.");
                }
            }
        }
    }

    internal static ArmResourceGetMonitorMetricsOptions BuildMonitorMetricsOptions(
        string metricNames,
        string resourceType,
        ObservationWindow window,
        TimeSpan interval,
        string aggregation,
        string? filter = null)
    {
        var options = new ArmResourceGetMonitorMetricsOptions
        {
            Metricnames = metricNames,
            Metricnamespace = resourceType,
            Timespan = window.Timespan,
            Interval = interval,
            Aggregation = aggregation
        };
        if (!string.IsNullOrEmpty(filter))
        {
            options.Filter = filter;
        }
        return options;
    }

    private async Task<ExistenceResult> GetTargetExistenceAsync(
        ResourceIdentifier resourceId,
        string apiVersion,
        string endpointName,
        string? tenant,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetArmAsync(
                resourceId.ToString(),
                apiVersion,
                tenant,
                cancellationToken);
            var observedAt = DateTimeOffset.UtcNow;
            if (response.IsSuccess)
            {
                return new ExistenceResult("exists", observedAt, null);
            }
            if (IsResourceNotFoundResponse(response.StatusCode, response.Content))
            {
                return new ExistenceResult("notFound", observedAt, null);
            }

            return new ExistenceResult(
                "indeterminate",
                observedAt,
                new RoutingDiagnosticError(
                    "targetArm",
                    "read",
                    resourceId.ToString(),
                    (int)response.StatusCode,
                    GetArmErrorCode(response.Content) ?? "Indeterminate",
                    "Reader",
                    "The current target resource existence could not be determined."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to check routed resource existence for endpoint '{EndpointName}'.",
                endpointName);
            return new ExistenceResult(
                "indeterminate",
                DateTimeOffset.UtcNow,
                CreateDiagnosticError(
                    ex,
                    "targetArm",
                    "read",
                    resourceId.ToString(),
                    "Reader",
                    "The current target resource existence could not be determined."));
        }
    }

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

    internal async Task<List<EndpointHealthData>> GetRoutingEndpointsHealthAsync(
        ResourceIdentifier hubResourceId,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var results = new List<EndpointHealthData>();
        string? nextLink = $"{hubResourceId}/routingEndpointsHealth";
        string? apiVersion = "2023-06-30";
        while (!string.IsNullOrEmpty(nextLink))
        {
            var response = await GetArmAsync(nextLink, apiVersion, tenant, cancellationToken);
            if (!response.IsSuccess)
            {
                throw new RequestFailedException(
                    (int)response.StatusCode,
                    "IoT Hub routing endpoint health could not be read.");
            }

            var page = JsonSerializer.Deserialize(
                response.Content,
                IoTHubJsonContext.Default.EndpointHealthDataListResult);
            results.AddRange(page?.Value ?? []);
            nextLink = page?.NextLink;
            apiVersion = null;
        }
        return results;
    }

    private async Task<ArmGetResult> GetArmAsync(
        string resourcePathOrUrl,
        string? apiVersion,
        string? tenant,
        CancellationToken cancellationToken)
    {
        var token = await GetArmAccessTokenAsync(tenant, cancellationToken);
        var managementEndpoint =
            AzureService.CloudConfiguration.ArmEnvironment.Endpoint.ToString().TrimEnd('/');
        var url = Uri.TryCreate(resourcePathOrUrl, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri.ToString()
            : $"{managementEndpoint}{resourcePathOrUrl}";
        if (!string.IsNullOrEmpty(apiVersion))
        {
            url += $"{(url.Contains('?') ? '&' : '?')}api-version={apiVersion}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await AzureService.GetClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return new ArmGetResult(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static bool IsResourceNotFoundResponse(
        HttpStatusCode statusCode,
        string? responseContent)
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
            return IsResourceNotFoundErrorCode(GetArmErrorCode(responseContent));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetArmErrorCode(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        using var document = JsonDocument.Parse(responseContent);
        return document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
    }

    private static bool IsResourceNotFoundErrorCode(string? errorCode) =>
        errorCode is not null
        && (errorCode.Equals("ResourceNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ParentResourceNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("EntityNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("MessagingEntityNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("ContainerNotFound", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("NotFound", StringComparison.OrdinalIgnoreCase));

    private async Task<GenericResourceData> GetIoTHubGenericResourceAsync(
        string hubName,
        string resourceGroup,
        string subscription,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken)
    {
        var subscriptionResource = await AzureService.GetSubscription(
            subscription,
            tenant,
            retryPolicy,
            cancellationToken);
        var armClient = await CreateArmClientAsync(
            tenant,
            retryPolicy,
            cancellationToken: cancellationToken);
        var resourceId = BuildResourceId(
            subscriptionResource.Data.SubscriptionId,
            resourceGroup,
            "Microsoft.Devices/IotHubs",
            hubName);
        var hub = await armClient.GetGenericResource(resourceId).GetAsync(cancellationToken);
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
            ConvertToRoutingEndpointDetailsList(properties?.Routing?.Endpoints));
    }

    private static List<RoutingEndpointDetails> ConvertToRoutingEndpointDetailsList(
        RoutingEndpoints? endpoints)
    {
        if (endpoints is null)
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

    private static void AppendEndpoints(
        List<RoutingEndpointDetails> results,
        List<RoutingEndpointProperties>? endpoints,
        string endpointType,
        bool isContainer)
    {
        foreach (var endpoint in endpoints ?? [])
        {
            results.Add(new RoutingEndpointDetails(
                Name: endpoint.Name ?? string.Empty,
                EndpointType: endpointType,
                EndpointResourceName: isContainer
                    ? GetEndpointResourceName(
                        endpoint.Id,
                        endpoint.EndpointUri,
                        endpoint.ContainerName,
                        preferEndpointUri: true)
                    : GetEndpointResourceName(
                        endpoint.Id,
                        endpoint.EndpointUri,
                        endpoint.EntityPath),
                SubscriptionId: endpoint.SubscriptionId,
                ResourceGroup: endpoint.ResourceGroup,
                EndpointUri: endpoint.EndpointUri,
                EntityPath: isContainer ? null : endpoint.EntityPath,
                ContainerName: isContainer ? endpoint.ContainerName : null,
                DatabaseName: endpointType == "CosmosDBSqlContainer" ? endpoint.DatabaseName : null,
                AuthenticationType: endpoint.AuthenticationType,
                BatchFrequencyInSeconds: endpoint.BatchFrequencyInSeconds,
                EndpointId: endpoint.Id));
        }
    }

    private static List<RoutingEndpointDetails> FilterEndpoints(
        List<RoutingEndpointDetails> endpoints,
        string? endpointName,
        string hubName)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            return endpoints;
        }

        var filtered = endpoints
            .Where(endpoint =>
                string.Equals(endpoint.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return filtered.Count > 0
            ? filtered
            : throw new KeyNotFoundException(
                $"Routing endpoint '{endpointName}' not found on IoT Hub '{hubName}'.");
    }

    private static ResolvedTarget? ResolveTarget(RoutingEndpointDetails endpoint)
    {
        var descriptor = GetTargetDescriptor(endpoint.EndpointType);
        if (descriptor is null ||
            string.IsNullOrWhiteSpace(endpoint.SubscriptionId) ||
            string.IsNullOrWhiteSpace(endpoint.ResourceGroup) ||
            string.IsNullOrWhiteSpace(endpoint.EndpointResourceName))
        {
            return null;
        }

        var parent = BuildResourceId(
            endpoint.SubscriptionId,
            endpoint.ResourceGroup,
            descriptor.ResourceType,
            endpoint.EndpointResourceName);
        ResourceIdentifier? routedResource = endpoint.EndpointType switch
        {
            "EventHub" when !string.IsNullOrWhiteSpace(endpoint.EntityPath)
                => new ResourceIdentifier($"{parent}/eventhubs/{endpoint.EntityPath}"),
            "ServiceBusQueue" when !string.IsNullOrWhiteSpace(endpoint.EntityPath)
                => new ResourceIdentifier($"{parent}/queues/{endpoint.EntityPath}"),
            "ServiceBusTopic" when !string.IsNullOrWhiteSpace(endpoint.EntityPath)
                => new ResourceIdentifier($"{parent}/topics/{endpoint.EntityPath}"),
            "StorageContainer" when !string.IsNullOrWhiteSpace(endpoint.ContainerName)
                => new ResourceIdentifier(
                    $"{parent}/blobServices/default/containers/{endpoint.ContainerName}"),
            "CosmosDBSqlContainer" when
                !string.IsNullOrWhiteSpace(endpoint.DatabaseName) &&
                !string.IsNullOrWhiteSpace(endpoint.ContainerName)
                => new ResourceIdentifier(
                    $"{parent}/sqlDatabases/{endpoint.DatabaseName}/containers/{endpoint.ContainerName}"),
            _ => null
        };
        return routedResource is null ? null : new ResolvedTarget(descriptor, parent, routedResource);
    }

    private static RoutingTargetDescriptor? GetTargetDescriptor(string endpointType) => endpointType switch
    {
        "EventHub" => new("EventHub", "Microsoft.EventHub/namespaces", "2024-01-01"),
        "ServiceBusQueue" => new("ServiceBusQueue", "Microsoft.ServiceBus/namespaces", "2024-01-01"),
        "ServiceBusTopic" => new("ServiceBusTopic", "Microsoft.ServiceBus/namespaces", "2024-01-01"),
        "StorageContainer" => new("StorageContainer", "Microsoft.Storage/storageAccounts", "2023-05-01"),
        "CosmosDBSqlContainer" => new("CosmosDBSqlContainer", "Microsoft.DocumentDB/databaseAccounts", "2024-05-15"),
        _ => null
    };

    private static IReadOnlyDictionary<string, string> GetDimensions(
        MonitorTimeSeriesElement series)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var metadata in series.Metadatavalues)
        {
            var name = metadata.Name?.Value;
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(metadata.Value) &&
                !string.Equals(metadata.Value, "__Empty", StringComparison.OrdinalIgnoreCase))
            {
                dimensions[name] = metadata.Value;
            }
        }
        return dimensions;
    }

    private static string? GetDimensionValue(MonitorTimeSeriesElement series, string name) =>
        series.Metadatavalues
            .FirstOrDefault(metadata =>
                string.Equals(metadata.Name?.Value, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static void AddDimensionIfPresent(
        MonitorTimeSeriesElement series,
        Dictionary<string, string> dimensions,
        string name)
    {
        var value = GetDimensionValue(series, name);
        if (!string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, "__Empty", StringComparison.OrdinalIgnoreCase))
        {
            dimensions[name] = value;
        }
    }

    private static void AddSharedError(
        Dictionary<string, List<RoutingDiagnosticError>> errors,
        RoutingDiagnosticError error)
    {
        foreach (var endpointErrors in errors.Values)
        {
            endpointErrors.Add(error);
        }
    }

    private static RoutingDiagnosticError CreateTargetMetricError(
        Exception exception,
        ResourceIdentifier resourceId,
        string metricName) => CreateDiagnosticError(
            exception,
            "targetAzureMonitor",
            metricName,
            resourceId.ToString(),
            "Monitoring Reader",
            $"Target Azure Monitor metric '{metricName}' could not be read.");

    private static RoutingDiagnosticError CreateDiagnosticError(
        Exception exception,
        string source,
        string operation,
        string? resourceId,
        string? requiredRole,
        string message)
    {
        var requestFailed = exception as RequestFailedException;
        return new RoutingDiagnosticError(
            source,
            operation,
            resourceId,
            requestFailed?.Status,
            requestFailed?.ErrorCode ?? exception.GetType().Name,
            requiredRole,
            message);
    }

    private static string GetQueryStatus(
        bool hasValues,
        IReadOnlyList<RoutingDiagnosticError> errors)
    {
        if (errors.Count == 0)
        {
            return "queried";
        }
        if (hasValues)
        {
            return "partial";
        }
        if (errors.Any(error => error.StatusCode is 401 or 403))
        {
            return "unauthorized";
        }
        return errors.Any(error => error.StatusCode == 404) ? "notFound" : "failed";
    }

    private static RoutingHubEmitted CreateEmptyHubEvidence() => new(
        new RoutingMetricEvidence("Microsoft.Devices/IotHubs", [], []),
        []);

    private static RoutingTargetEmitted CreateEmptyTargetEvidence(string status) => new(
        status,
        "parentResource",
        null,
        [],
        [],
        []);

    private static bool IsAuthorizationError(Exception exception) =>
        exception is RequestFailedException requestFailed &&
            requestFailed.Status is 401 or 403
        || exception is Azure.Identity.AuthenticationFailedException
        || exception.Message.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotFoundException(Exception exception) =>
        exception is RequestFailedException requestFailed && requestFailed.Status == 404;

    private static void ValidateInputs(
        string hubName,
        string resourceGroup,
        string subscription) =>
        ValidateRequiredParameters(
            (nameof(subscription), subscription),
            (nameof(resourceGroup), resourceGroup),
            (nameof(hubName), hubName));

    private static ResourceIdentifier BuildResourceId(
        string subscription,
        string resourceGroup,
        string resourceType,
        string resourceName) =>
        new($"/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/{resourceType}/{resourceName}");

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
        return TryGetEndpointHostName(endpointUri, out var endpointName) ? endpointName : fallback;
    }

    private static bool TryGetEndpointHostName(string? endpointUri, out string? endpointName)
    {
        endpointName = null;
        if (!Uri.TryCreate(endpointUri, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }
        endpointName = uri.Host.Split('.')[0];
        return !string.IsNullOrWhiteSpace(endpointName);
    }

    private static string ToIsoString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

    private static readonly IReadOnlyDictionary<string, string> EmptyDimensions =
        new Dictionary<string, string>();

    internal readonly record struct ObservationWindow(
        DateTimeOffset StartTime,
        DateTimeOffset EndTime)
    {
        public string Timespan => $"{ToIsoString(StartTime)}/{ToIsoString(EndTime)}";
    }

    private sealed record RoutingTargetDescriptor(
        string EndpointType,
        string ResourceType,
        string ApiVersion);

    private sealed record ResolvedTarget(
        RoutingTargetDescriptor Descriptor,
        ResourceIdentifier ParentResourceId,
        ResourceIdentifier RoutedResourceId);

    private sealed record ExistenceResult(
        string Status,
        DateTimeOffset ObservedAt,
        RoutingDiagnosticError? Error);

    private sealed record ArmGetResult(HttpStatusCode StatusCode, string Content)
    {
        public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
    }
}
