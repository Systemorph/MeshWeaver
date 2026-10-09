#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.ImageClosures;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins #4066: an image's closure becomes mesh data written from the CD build's signed record.
/// The pair that matters is <see cref="SignedRecord_IsWrittenAtItsDigest_AndTheDeliveryIsConsumed"/>
/// versus <see cref="TamperedBody_IsRefused_NothingWritten"/> — the second is the negative control:
/// a record whose bytes changed after signing must never become data a gate trusts. And
/// <see cref="Coverage_NamesEveryMissingDigest_AsNotMeasured"/> pins the reader rule: absent is
/// NOT MEASURED, never clean.
/// </summary>
public class ImageClosureIngestTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SecretKey = "Test:PlatformWebhookSecret";
    private const string Secret = "cd-shared-secret";
    private static readonly string DigestA = "sha256:" + new string('a', 64);
    private static readonly string DigestB = "sha256:" + new string('b', 64);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddWebhookInbox()
            .AddImageClosures()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SecretKey] = Secret,
                }).Build()));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string Record(string digest, int fileCount = 2, int filesCarried = 2) =>
        JsonSerializer.Serialize(new
        {
            @event = ImageClosureRecord.EventName,
            repository = "memex-portal-ai",
            digest,
            tags = new[] { "stg-9", "stg-9", "" },
            platformCommit = "0123abc",
            platformVersion = "3.1.10400",
            frameworkIdentity = "c003e001",
            runUrl = "https://github.com/Systemorph/MeshWeaver/actions/runs/1",
            platforms = new[]
            {
                new
                {
                    rid = "linux-x64",
                    fileCount,
                    manifestSha256 = new string('c', 64),
                    files = Enumerable.Range(0, filesCarried).Select(i => new
                    {
                        path = $"Lib{i}.dll",
                        sha256 = new string((char)('0' + i % 10), 64),
                        bytes = 100L + i,
                    }).ToArray(),
                },
            },
        });

    private static KeyValuePair<string, string> Sign(string body, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return new(WebhookInbox.SignatureHeader, $"sha256={hex}");
    }

    private static Task<T> Wait<T>(IObservable<T> source, CancellationToken ct) =>
        source.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    // 🚨 RunAsSystem, never `Observable.Using(ImpersonateAsSystem, …)`: Using opens the AsyncLocal scope
    // on the subscribing thread and disposes it wherever the result lands, which can leave the test
    // flow latched as System (#1790).
    private Task<MeshNode> WriteAsSystem(MeshNode node, CancellationToken ct) =>
        Wait(Access.RunAsSystem(() => MeshService.CreateOrUpdateNode(node)), ct);

    /// <summary>A node that MUST exist: read from its authoritative node stream (never an eventually
    /// consistent query), waiting for it to be there.</summary>
    private Task<MeshNode> ReadPresent(string path, CancellationToken ct) =>
        Wait(Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Where(n => n is not null).Select(n => n!), ct);

    /// <summary>A node that must be GONE (or never appear): its parent's <c>scope:children</c> listing,
    /// re-read until the path is absent — so a stale index cannot answer "still there" after a delete.</summary>
    private Task<bool> WaitAbsent(string path, CancellationToken ct)
    {
        var parent = path[..path.LastIndexOf('/')];
        return Wait(Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Access.RunAsSystem(() => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{parent} scope:children")).Take(1)))
            .Select(c => c.Items.All(n => n.Path != path))
            .Where(absent => absent), ct);
    }

    /// <summary>Delivers through the REAL inbox (allowlist, signature, owner-node check) and returns the stored event.</summary>
    private async Task<MeshNode> Deliver(string body, KeyValuePair<string, string> signature, CancellationToken ct)
    {
        await WriteAsSystem(ImageClosureNodes.IndexNode(), ct);
        var result = await WebhookInbox.Deliver(
                Mesh, [new WebhookInbox.WebhookTarget(ImageClosureNodes.InboxTarget, SecretKey)],
                ImageClosureNodes.InboxTarget, "application/json", [signature], body)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        return await ReadPresent(result.NodePath!, ct);
    }

    private Task<string?> Drain(MeshNode eventNode, CancellationToken ct, string? secret = Secret) =>
        Wait(ImageClosureIngest.Drain(Mesh, eventNode, secret, null), ct);

    [Fact(Timeout = 120000)]
    public async Task SignedRecord_IsWrittenAtItsDigest_AndTheDeliveryIsConsumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Record(DigestA);
        var evt = await Deliver(body, Sign(body, Secret), ct);

        var path = await Drain(evt, ct);

        path.Should().Be(ImageClosureNodes.PathOf("memex-portal-ai", DigestA));
        path.Should().Be($"Admin/ImageClosures/memex-portal-ai-{new string('a', 64)}", "the digest IS the identity");
        var node = await ReadPresent(path!, ct);
        node.NodeType.Should().Be(ImageClosureNodes.NodeType);
        var closure = node.ContentAs<ImageClosure>(Mesh.JsonSerializerOptions)!;
        closure.Digest.Should().Be(DigestA);
        closure.Tags.Should().Equal("stg-9");
        closure.PlatformVersion.Should().Be("3.1.10400", "the version is carried opaque — no -ci notation assumed");
        closure.FrameworkIdentity.Should().Be("c003e001");
        closure.Platforms.Should().ContainSingle().Which.FileCount.Should().Be(2);
        closure.Platforms[0].Files.Select(f => f.Path).Should().Equal("Lib0.dll", "Lib1.dll");
        closure.FileCount.Should().Be(2);
        (await WaitAbsent(evt.Path, ct)).Should().BeTrue("the delete is the acknowledgement");
    }

    [Fact(Timeout = 120000)]
    public async Task TamperedBody_IsRefused_NothingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        // Signed over one body, stored with another: the inbox's own check is bypassed by writing the
        // event directly, so THIS consumer's re-verification is what is under test.
        var signed = Record(DigestA);
        var stored = Record(DigestB);
        await WriteAsSystem(ImageClosureNodes.IndexNode(), ct);
        var evt = await WriteAsSystem(new MeshNode(Guid.NewGuid().ToString("N"), $"{ImageClosureNodes.InboxTarget}/{WebhookInbox.InboxContainer}")
        {
            NodeType = WebhookInbox.NodeType,
            MainNode = ImageClosureNodes.InboxTarget,
            Content = new WebhookEvent
            {
                ReceivedAt = DateTimeOffset.UtcNow,
                ContentType = "application/json",
                Headers = ImmutableDictionary<string, string>.Empty.Add(Sign(signed, Secret).Key, Sign(signed, Secret).Value),
                Body = stored,
            },
        }, ct);

        (await Drain(evt, ct)).Should().BeNull();
        (await WaitAbsent(ImageClosureNodes.PathOf("memex-portal-ai", DigestB), ct)).Should().BeTrue("an unverified record must never become data");
        (await WaitAbsent(ImageClosureNodes.PathOf("memex-portal-ai", DigestA), ct)).Should().BeTrue();
        (await WaitAbsent(evt.Path, ct)).Should().BeTrue("a refusal is consumed, never re-drained forever");
    }

    [Fact(Timeout = 120000)]
    public async Task NoSecretOnThisInstance_RefusesEvenAValidRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Record(DigestA);
        var evt = await Deliver(body, Sign(body, Secret), ct);
        (await Drain(evt, ct, secret: "")).Should().BeNull();
        (await WaitAbsent(ImageClosureNodes.PathOf("memex-portal-ai", DigestA), ct)).Should().BeTrue();
    }

    [Fact]
    public void TruncatedRecord_IsRefused_NeverASmallerClosure()
    {
        ImageClosureRecord.TryParse(Record(DigestA, fileCount: 3, filesCarried: 2), DateTimeOffset.UtcNow, out var why)
            .Should().BeNull();
        why.Should().Contain("fileCount 3").And.Contain("2 file(s)");
        // Control: the same record with an honest denominator parses.
        ImageClosureRecord.TryParse(Record(DigestA, fileCount: 2, filesCarried: 2), DateTimeOffset.UtcNow, out _)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[null]}""", "null")]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"MANIFEST","files":null}]}""", "no file list")]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"MANIFEST","files":[null]}]}""", "null file")]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"","files":[{"path":"a.dll","sha256":"FILE","bytes":1}]}]}""", "manifest hash")]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"nothex","files":[{"path":"a.dll","sha256":"FILE","bytes":1}]}]}""", "manifest hash")]
    [InlineData("""{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"MANIFEST","files":[{"path":"a.dll","sha256":"FILE"}]}]}""", "no size")]
    public void ANullOrMalformedShape_IsRefused_NeverDereferenced(string template, string expectedWhy)
    {
        var body = template.Replace("DIGEST", DigestA).Replace("MANIFEST", new string('c', 64)).Replace("FILE", new string('d', 64));
        ImageClosureRecord.TryParse(body, DateTimeOffset.UtcNow, out var why).Should().BeNull();
        why.Should().Contain(expectedWhy);
        // Control: the same shape, every member present and well-formed, parses.
        var good = """{"event":"image-closure","repository":"r","digest":"DIGEST","platforms":[{"rid":"linux-x64","fileCount":1,"manifestSha256":"MANIFEST","files":[{"path":"a.dll","sha256":"FILE","bytes":1}]}]}"""
            .Replace("DIGEST", DigestA).Replace("MANIFEST", new string('c', 64)).Replace("FILE", new string('d', 64));
        ImageClosureRecord.TryParse(good, DateTimeOffset.UtcNow, out var none).Should().NotBeNull(none);
    }

    [Theory]
    [InlineData("3.1.10400")]
    [InlineData("sha256:ABC")]
    [InlineData("")]
    public void ADigestThatIsNotTheIdentityShape_IsRefused(string digest)
    {
        ImageClosureRecord.TryParse(Record(digest), DateTimeOffset.UtcNow, out var why).Should().BeNull();
        why.Should().Contain("identity");
    }

    [Fact(Timeout = 120000)]
    public async Task Coverage_NamesEveryMissingDigest_AsNotMeasured()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Record(DigestA);
        await Drain(await Deliver(body, Sign(body, Secret), ct), ct);
        var a = new ExpectedImage("memex-portal-ai", DigestA);
        var b = new ExpectedImage("memex-portal-ai", DigestB);

        var partial = await ImageClosureCoverage.Read(Mesh, [a, b])
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        partial.AllMeasured.Should().BeFalse("an absent record is NOT MEASURED, never clean");
        partial.Measured.Should().ContainSingle().Which.Digest.Should().Be(DigestA);
        partial.NotMeasured.Should().Equal(b);
        partial.Statement.Should().Contain("1 of 2").And.Contain("NOT MEASURED").And.Contain(DigestB);

        // Control: asking only for what was written is fully measured.
        var full = await ImageClosureCoverage.Read(Mesh, [a])
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        full.AllMeasured.Should().BeTrue();
        full.Statement.Should().Contain("1 of 1");

        // Nothing asked is not a verdict.
        ImageClosureCoverage.Of([], []).AllMeasured.Should().BeFalse();
    }

    [Fact]
    public void Arming_RequiresTheTargetAndItsSecretKey()
    {
        static IConfiguration Config(params (string Key, string Value)[] pairs) =>
            new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

        ImageClosureIngest.SecretConfigKeyOf(Config(("WebhookInbox:Targets:0", "Store/Payments")))
            .Should().BeNull("not this instance's business");
        ImageClosureIngest.SecretConfigKeyOf(Config(("WebhookInbox:Targets:0", ImageClosureNodes.InboxTarget)))
            .Should().Be("", "allowlisted without a key is the misconfiguration the ingest refuses");
        ImageClosureIngest.SecretConfigKeyOf(Config(
                ("WebhookInbox:Targets:0", ImageClosureNodes.InboxTarget),
                ("WebhookInbox:Targets:0:SecretConfigKey", "Hosting:PlatformWebhookSecret")))
            .Should().Be("Hosting:PlatformWebhookSecret");
    }
}
