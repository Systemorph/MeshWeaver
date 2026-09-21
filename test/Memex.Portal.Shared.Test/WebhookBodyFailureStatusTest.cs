using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Social;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 What the ENDPOINTS answer when the body read fails — which is the property
/// <see cref="BoundedBodyTest"/> deliberately cannot reach (#4860).
///
/// <para>Those tests pin the reader: a client that vanishes mid-body must surface an exception
/// rather than the <c>null</c> that means over-the-cap. Necessary, and not sufficient — an endpoint
/// regressing to an unhandled 500, or answering a fixed status, would leave every one of them
/// green. Caught in review on this PR, which is exactly the shape of a suite that cannot fail for
/// the thing it was written for.</para>
///
/// <para><b>Two statuses, and they must not collapse into one.</b>
/// <see cref="BadHttpRequestException"/> is raised by Kestrel for BOTH "Unexpected end of request
/// content" (400) and a <c>MaxRequestBodySize</c> breach (413). Answering a fixed 400 would report
/// a server-limit breach as a bad request — and these endpoints already answer 413 for their OWN
/// cap, so the same oversized delivery would report two different statuses depending only on which
/// limit noticed it first.</para>
/// </summary>
public class WebhookBodyFailureStatusTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]   // the truncated delivery this issue was filed on
    [InlineData(StatusCodes.Status413PayloadTooLarge)] // Kestrel's own body-size refusal
    public async Task TheGitHubEndpoint_AnswersTheBodyReadsOwnStatus(int raised)
    {
        await using var app = await StartHost(a => a.MapGitHubWebhook(), raised);
        var response = await Post(app, "/webhooks/github");

        Assert.Equal((HttpStatusCode)raised, response.StatusCode);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task TheWebhookInbox_AnswersTheBodyReadsOwnStatus(int raised)
    {
        // The OTHER call site. The incident only ever named the GitHub endpoint, but both read
        // through the same BoundedBody, so a fix proved on one says nothing about the other.
        await using var app = await StartHost(a => a.MapWebhookInbox(), raised);
        var response = await Post(app, "/api/hooks/anything");

        Assert.Equal((HttpStatusCode)raised, response.StatusCode);
    }

    /// <summary>
    /// A real host over the REAL mesh (no doubles), with one piece of middleware that swaps the
    /// request body for a stream failing the way Kestrel does. That is what makes the endpoint's own
    /// catch run — reaching it from outside would need a real socket to be severed mid-request.
    /// </summary>
    private async Task<WebApplication> StartHost(Action<WebApplication> map, int raised)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Without a secret the GitHub endpoint answers 503 BEFORE the body is read, and the
            // test would pass having exercised nothing.
            ["GitHub:Webhook:Secret"] = "test-secret",
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<IMeshService>());
        builder.Services.AddSingleton<GitHubWebhookProcessor>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Request.Body = new FailingBody(raised);
            await next(context);
        });
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpResponseMessage> Post(WebApplication app, string route) =>
        app.GetTestClient().PostAsync(
            route, new StringContent(""), TestContext.Current.CancellationToken);

    /// <summary>Fails on the first read with the status Kestrel would carry.</summary>
    private sealed class FailingBody(int statusCode) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => throw new BadHttpRequestException("Unexpected end of request content.", statusCode);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new BadHttpRequestException("Unexpected end of request content.", statusCode);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
