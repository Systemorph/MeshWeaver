using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The refusal contract of <c>POST /api/mesh/upload</c>'s multipart read.
///
/// <para><b>Why it exists.</b> <c>HandleUpload</c> used to be one <c>async</c> method that read the
/// form, validated it, and then <c>await</c>ed <c>MeshOperations.Upload</c> — an await chain
/// reaching into hub code, which is the shape an endpoint's <c>Task&lt;IResult&gt;</c> signature is
/// not licence for. Splitting it moved the three refusals across a new seam
/// (<c>ReadUploadAsync</c> returns them; the composed chain ships them without ever constructing a
/// session hub), and a refusal that quietly became a 500 — or one that started resolving a session
/// before deciding the request was malformed — would look like working code from anywhere else.</para>
///
/// <para>Asserted at that seam rather than over HTTP on purpose: the route is Bearer-only, so an
/// end-to-end 400 test would need the whole token pipeline and a live mesh in order to prove
/// something about a branch that reaches neither.</para>
/// </summary>
public class MeshApiUploadBodyTest
{
    [Fact]
    public async Task A_non_multipart_request_is_refused_before_the_body_is_read()
    {
        var http = NewContext();
        http.Request.ContentType = "application/json";

        var (failure, path, bytes) = await MeshApiEndpoints.ReadUploadAsync(
            http, TestContext.Current.CancellationToken);

        StatusOf(failure).Should().Be((int)HttpStatusCode.BadRequest);
        path.Should().BeNull();
        bytes.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_path_field_is_refused()
    {
        var http = NewMultipart(path: null, fileName: "logo.png", content: "png-bytes");

        var (failure, path, bytes) = await MeshApiEndpoints.ReadUploadAsync(
            http, TestContext.Current.CancellationToken);

        StatusOf(failure).Should().Be((int)HttpStatusCode.BadRequest);
        path.Should().BeNull();
        bytes.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var http = NewMultipart(path: "@Foo/content/logo.png", fileName: null, content: null);

        var (failure, path, bytes) = await MeshApiEndpoints.ReadUploadAsync(
            http, TestContext.Current.CancellationToken);

        StatusOf(failure).Should().Be((int)HttpStatusCode.BadRequest);
        path.Should().BeNull();
        bytes.Should().BeNull();
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var http = NewMultipart(path: "@Foo/content/logo.png", fileName: "logo.png", content: "");

        var (failure, path, bytes) = await MeshApiEndpoints.ReadUploadAsync(
            http, TestContext.Current.CancellationToken);

        StatusOf(failure).Should().Be((int)HttpStatusCode.BadRequest);
        path.Should().BeNull();
        bytes.Should().BeNull();
    }

    /// <summary>
    /// The success shape: no failure, the declared path, and the file's bytes VERBATIM — including
    /// a non-ASCII byte, since these bytes are what reaches <c>MeshOperations.Upload</c> and a
    /// re-encode here would land a corrupt asset rather than fail.
    /// </summary>
    [Fact]
    public async Task A_well_formed_upload_yields_the_path_and_the_exact_bytes()
    {
        var body = "the-file-contents ÿ";
        var http = NewMultipart(path: "@Foo/content/logo.png", fileName: "logo.png", content: body);

        var (failure, path, bytes) = await MeshApiEndpoints.ReadUploadAsync(
            http, TestContext.Current.CancellationToken);

        failure.Should().BeNull();
        path.Should().Be("@Foo/content/logo.png");
        bytes.Should().Equal(Encoding.UTF8.GetBytes(body));
    }

    private static int? StatusOf(IResult? result) =>
        (result as IStatusCodeHttpResult)?.StatusCode;

    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    /// <summary>A multipart/form-data request carrying the given (optional) field and file.</summary>
    private static DefaultHttpContext NewMultipart(string? path, string? fileName, string? content)
    {
        var http = NewContext();
        var files = new FormFileCollection();
        var fields = new Dictionary<string, StringValues>();

        if (path is not null)
            fields["path"] = path;

        if (fileName is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            files.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
            });
        }

        http.Request.ContentType = "multipart/form-data; boundary=----test";
        http.Request.Form = new FormCollection(fields, files);
        return http;
    }
}
