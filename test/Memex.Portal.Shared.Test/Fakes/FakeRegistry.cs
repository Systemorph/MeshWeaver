using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.Test.Fakes;

/// <summary>
/// A registry in-process — the two routes the first-run wizard calls, and nothing else.
///
/// <para>🚨 <b>Why a fake and not the real registry:</b> registering claims an instance id
/// GLOBALLY, and the platform states an id is never re-issued even after deletion. A suite pointed
/// at a live registry would burn a real, permanent id on every run, on shared infrastructure. So the
/// wire contract is served here while the client under test stays the REAL one — real
/// serialization, real status handling, real refusal paths.</para>
///
/// <para>It can be told to refuse, because the refusals are the interesting half: a taken id, a
/// rejected key and an unreachable host each have a different fix, and the wizard has to say
/// which.</para>
/// </summary>
public sealed class FakeRegistry : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Status to answer registration with. 200 by default.</summary>
    public HttpStatusCode RegisterStatus { get; init; } = HttpStatusCode.OK;

    /// <summary>Status to answer the package listing with. 200 by default.</summary>
    public HttpStatusCode ListStatus { get; init; } = HttpStatusCode.OK;

    /// <summary>The plan the fake enrols an open registration on.</summary>
    public string Plan { get; init; } = "free";

    /// <summary>The key it issues. Empty simulates "accepted but returned no key".</summary>
    public string IssuedKey { get; init; } = "mwi_faketestkey";

    /// <summary>What the listing returns.</summary>
    public ImmutableList<PackageManifest> Packages { get; init; } =
    [
        new PackageManifest { Id = "Plugins/Store", Name = "Store", Description = "Browse and install plugins." },
        // The one that answers the DATABASE question — the flow's whole point.
        new PackageManifest
        {
            Id = "Plugins/PostgreSql",
            Name = "PostgreSQL",
            Description = "Durable storage on a Postgres server you run.",
            StorageType = "PostgreSql",
        },
        // A storage package for a backend the image CANNOT open — it must appear in the plugin list
        // but must NOT be offered as a database, or the instance records a backend that never
        // resolves and fails at the next boot with the wizard gone.
        new PackageManifest
        {
            Id = "Plugins/Exotic",
            Name = "Exotic DB",
            Description = "A backend this image does not ship.",
            StorageType = "ExoticDb",
        },
    ];

    /// <summary>Every registration this fake was asked to perform, for assertions.</summary>
    public List<InstanceRegistrationPayloads.Request> Registrations { get; } = [];

    /// <summary>The bearer tokens the listing was called with, for assertions.</summary>
    public List<string?> ListTokens { get; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (path == InstanceRegistrationPayloads.Route && request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadFromJsonAsync<InstanceRegistrationPayloads.Request>(
                Json, cancellationToken);
            if (body is not null) Registrations.Add(body);
            if (RegisterStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(RegisterStatus)
                {
                    Content = new StringContent("registration refused by the fake registry"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new InstanceRegistrationPayloads.Response(body?.InstanceId ?? "", IssuedKey) { Plan = Plan },
                    options: Json),
            };
        }

        if (path == RegistryPackageSource.RoutePrefix && request.Method == HttpMethod.Get)
        {
            ListTokens.Add(request.Headers.Authorization?.Parameter);
            if (ListStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(ListStatus)
                {
                    Content = new StringContent("listing refused by the fake registry"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { packages = Packages }, options: Json),
            };
        }

        // Anything else is a route the wizard should not be calling. Say so loudly rather than
        // answering 200 and letting a wrong call look correct.
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"the fake registry serves no {request.Method} {path}"),
        };
    }
}
