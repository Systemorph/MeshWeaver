using System;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Shared harness for the <c>MapMeshWeaver()</c> boot gate: a minimal web application whose
/// <see cref="IMessageHub"/> is the REAL mesh built by the test base, so the gate reads real hub
/// configuration rather than a stand-in.
/// </summary>
public static class BootGateHost
{
    /// <summary>Builds an unstarted app wired to <paramref name="mesh"/>.</summary>
    public static WebApplication For(IMessageHub mesh)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mesh);
        return builder.Build();
    }
}

/// <summary>
/// 🚨 THE BOOT GATE on <c>MapMeshWeaver()</c>. Mapping those routes publishes mesh content over
/// HTTP, so this is where the host has to prove it actually has an access evaluator.
///
/// <para><b>What it protects against.</b> With no <c>EffectivePermissionsDelegate</c> registered,
/// every permission check resolves to the default evaluator and answers <c>Permission.All</c>, and
/// <c>AddAccessControlPipeline</c> is not installed either (both come from the one
/// <c>AddRowLevelSecurity()</c> call). Anonymous callers are still refused — the content and SEO
/// routes run through <c>AnonymousGate</c>, which is independently fail-closed — but every
/// SIGNED-IN caller reads every partition, nothing logs it, and the portal looks healthy.</para>
///
/// <para>The companion class below has the SAME evaluator state — none — and boots, because it
/// declared the intent. That pair is the whole point: running ungated is a statement, never an
/// inference from a missing value.</para>
/// </summary>
public class MapMeshWeaverRefusesUndeclaredUngatedMeshTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Deliberately NOT <c>ConfigureMeshBase</c> — that installs the evaluator.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddGraph()
            .ConfigureHub(c => c.WithQuiesceTimeout(TestQuiesceTimeout));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// THE REGRESSION PIN: previously this mapped the routes and served.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void MappingWithNoEvaluator_AndNoDeclaration_RefusesToBoot()
    {
        using var app = BootGateHost.For(Mesh);

        Action act = () => app.MapMeshWeaver();

        act.Should().Throw<InvalidOperationException>(
                "publishing mesh content with no evaluator hands every signed-in caller every "
                + "partition — that must fail at startup, not quietly serve")
            .WithMessage("*AddRowLevelSecurity*", "the fix has to be named, not guessed at");
    }
}

/// <summary>
/// The escape hatch, and the reason it is a named declaration rather than the absence of a value:
/// identical evaluator state to the test above, opposite outcome.
///
/// <para>Without a way to say it, hosts that legitimately run ungated — a single-user sidecar, an
/// embedded instance — would have to disable the check instead, which is how a safety gate becomes
/// a gate nobody runs.</para>
/// </summary>
public class MapMeshWeaverAllowsDeclaredUngatedMeshTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddGraph()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .AllowUnsecuredMesh("single-user sidecar; no partitions to separate"));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 30000)]
    public void MappingWithAnExplicitDeclaration_Boots()
    {
        using var app = BootGateHost.For(Mesh);

        Action act = () => app.MapMeshWeaver();

        act.Should().NotThrow(
            "a host that says out loud that it is ungated is exercising a supported configuration");
    }
}

/// <summary>
/// The over-strictness catcher: a properly secured mesh must still map. Without it, "throws always"
/// would satisfy the refusal test and break every real portal. (The default
/// <c>MonolithMeshTestBase</c> mesh is the secured shape every portal uses.)
/// </summary>
public class MapMeshWeaverAllowsSecuredMeshTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public void MappingWithAnEvaluator_Boots()
    {
        using var app = BootGateHost.For(Mesh);

        Action act = () => app.MapMeshWeaver();

        act.Should().NotThrow("this is the normal, secured configuration every portal uses");
    }
}
