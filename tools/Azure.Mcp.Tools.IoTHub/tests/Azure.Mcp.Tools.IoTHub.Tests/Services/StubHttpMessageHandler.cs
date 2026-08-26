// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;

namespace Azure.Mcp.Tools.IoTHub.Tests.Services;

internal sealed class StubHttpMessageHandler(
    HttpStatusCode statusCode,
    string responseContent) : HttpMessageHandler
{
    public HttpMethod? RequestMethod { get; private set; }
    public Uri? RequestUri { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestMethod = request.Method;
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;

        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseContent),
        });
    }
}
