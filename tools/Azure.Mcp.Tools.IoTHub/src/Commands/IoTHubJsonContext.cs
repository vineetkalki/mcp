// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Mcp.Tools.IoTHub.Commands.IoTHub;
using Azure.Mcp.Tools.IoTHub.Commands.Routing;
using Azure.Mcp.Tools.IoTHub.Models;

namespace Azure.Mcp.Tools.IoTHub.Commands;

[JsonSerializable(typeof(IoTHubDescription))]
[JsonSerializable(typeof(IoTHubGetCommand.IoTHubGetCommandResult))]
[JsonSerializable(typeof(IoTHubProperties))]
[JsonSerializable(typeof(DeviceIdentity))]
[JsonSerializable(typeof(List<DeviceIdentity>))]
[JsonSerializable(typeof(DeviceListResult))]
[JsonSerializable(typeof(DeviceTwin))]
[JsonSerializable(typeof(List<DeviceTwin>))]
[JsonSerializable(typeof(IoTHubRegistryStatistics))]
[JsonSerializable(typeof(IoTHubQueryRequest))]
[JsonSerializable(typeof(IoTHubQueryPage))]
[JsonSerializable(typeof(IoTHubQueryRunResult))]
[JsonSerializable(typeof(QueryCompileRequest))]
[JsonSerializable(typeof(QueryPredicate))]
[JsonSerializable(typeof(List<QueryPredicate>))]
[JsonSerializable(typeof(QueryDiscoveredField))]
[JsonSerializable(typeof(List<QueryDiscoveredField>))]
[JsonSerializable(typeof(QueryDiscoveredFields))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(RoutingEndpointHealth))]
[JsonSerializable(typeof(RoutingEndpointImpactDetails))]
[JsonSerializable(typeof(RoutingTargetResourceSignals))]
[JsonSerializable(typeof(RoutingTargetConfigurationSignals))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
[JsonSerializable(typeof(RoutingEndpointDetails))]
[JsonSerializable(typeof(RoutingEndpointExploration))]
[JsonSerializable(typeof(RoutingEndpointStatus))]
[JsonSerializable(typeof(RoutingEndpointLatency))]
[JsonSerializable(typeof(LatencyTrendPoint))]
[JsonSerializable(typeof(IReadOnlyList<LatencyTrendPoint>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(RoutingEndpointHealthGetCommand.RoutingEndpointHealthGetCommandResult))]
[JsonSerializable(typeof(RoutingLatencyGetCommand.RoutingLatencyGetCommandResult))]
[JsonSerializable(typeof(RoutingDiagnoseCommand.RoutingDiagnoseCommandResult))]
[JsonSerializable(typeof(EndpointHealthDataListResult))]
[JsonSerializable(typeof(EndpointHealthData))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class IoTHubJsonContext : JsonSerializerContext
{
}
