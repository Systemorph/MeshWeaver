using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <see cref="SecretSealingKeyService"/> is a mesh singleton, so any code holding a hub resolves it — in-mesh
/// NodeType source included — and it reads the private key as System. Opening a sealed secret and rotating the key
/// are therefore authorized as the CALLER first (Doc/Architecture/AuthorizeAsCallerExecuteAsSystem) and only then
/// executed as System.
///
/// <para>This mesh grants NO public admin (<c>ConfigureMeshBase</c>): the DevLogin identity is the one
/// administrator, and the outsider holds nothing. Each refusal is followed by the administrator's same call, which
/// works — so the refusal is the gate, not a call that could never have succeeded.</para>
/// </summary>
public class SecretSealingAuthorizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Secret = "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ere";
    private const string Context = "memex-cloud|Google--OAuth--ClientSecret";

    private static readonly AccessContext Outsider = new()
    {
        ObjectId = "sealing-outsider", Name = "Sealing Outsider", Email = "sealing-outsider@meshweaver.io",
    };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddPluginCatalog();

    [Fact(Timeout = 180_000)]
    public async Task AnOrdinaryCaller_CannotOpenASealedSecret_NorRotateTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Mesh.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        ActAs(TestUsers.Admin);
        var key = await service.PublicKey().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        var envelope = SecretEnvelope.Seal(Secret, Convert.FromBase64String(key.PublicKey), key.KeyId, Context).ToJson();

        ActAs(Outsider);
        var open = () => service.Unseal(envelope, Context).Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        await open.Should().ThrowAsync<UnauthorizedAccessException>("opening a sealed secret needs Execute on the key node");
        var rotate = () => service.Rotate(Outsider.ObjectId!).Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        await rotate.Should().ThrowAsync<UnauthorizedAccessException>("rotating the key needs Update on the key node");

        ActAs(TestUsers.Admin);
        (await service.PublicKey().Timeout(TimeSpan.FromSeconds(60)).Await(ct)).KeyId
            .Should().Be(key.KeyId, "the refused rotation rotated nothing");
        (await service.Unseal(envelope, Context).Timeout(TimeSpan.FromSeconds(60)).Await(ct))
            .Should().Be(Secret, "the administrator's same call opens it");
        (await service.Rotate("admin").Timeout(TimeSpan.FromSeconds(60)).Await(ct)).KeyId
            .Should().NotBe(key.KeyId, "the administrator's same call rotates it");
    }

    /// <summary>
    /// 🚨 The elevated work never runs inside the permission fold's gate. On a warm cache the fold emits during
    /// <c>Subscribe</c> while holding its Rx gates (issue #899), so a bare <c>Take(1)</c> would read the key — and
    /// write it, for a rotation — under those locks. This decision emits exactly that way: inside a held lock, on the
    /// subscribing thread.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheElevatedWork_RunsOutsideThePermissionFoldsGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var foldGate = new object();
        var decision = Observable.Create<PermissionCheckOutcome>(observer =>
        {
            lock (foldGate) observer.OnNext(PermissionCheckOutcome.Granted);
            return () => { };
        });

        var ranInsideGate = await SecretSealingKeyService
            .AuthorizedThen(decision, Permission.Execute, "open a sealed secret",
                () => Observable.Return(Monitor.IsEntered(foldGate)))
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(ct);

        ranInsideGate.Should().BeFalse("the decision is taken inside the fold, the work it authorizes runs outside it");
    }

    /// <summary>The identity the next call is made as: the host identity and the flow's context.</summary>
    private void ActAs(AccessContext identity)
    {
        TestUsers.DevLogin(Mesh, identity);
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetContext(identity);
    }
}
