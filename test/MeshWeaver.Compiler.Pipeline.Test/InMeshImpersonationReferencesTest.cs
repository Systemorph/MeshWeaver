using System;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Option C of Doc/Architecture/InMeshImpersonation: the compiler finds every reference in
/// in-mesh source to an API that lets code act as someone else — including the shapes the RUNTIME
/// guard cannot see: a call in tail position (the JIT erases the frame), a method group handed to a
/// platform subscriber (no in-mesh frame left), and a hand-built context passed to an API that is
/// not a surface.
///
/// <para>Negative control: <see cref="OrdinaryIdentityCode_IsNotReported"/> — the legitimate
/// in-mesh shapes (switching back to a captured viewer, READING a query's UserId or a context's
/// IsHub) must produce nothing, or the check would bury the escalations it exists to name.</para>
/// </summary>
public class InMeshImpersonationReferencesTest
{
    private const string Usings = """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Messaging;
        using MeshWeaver.Mesh.Security;
        using MeshWeaver.Mesh.Services;
        using MeshWeaver.ContentCollections;
        """;

    private static Compilation Compile(string body)
    {
        // Touch every assembly the snippets bind against so it is loaded and has a Location.
        _ = new[] { typeof(AccessService), typeof(WellKnownUsers), typeof(MeshQueryRequest),
                    typeof(ContentImportBuilder), typeof(System.Reactive.Linq.Observable) };
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create("InMeshProbe",
            [CSharpSyntaxTree.ParseText($"{Usings}\npublic static class Probe {{\n{body}\n}}", path: "Probe.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "the probe must bind, or the scan proves nothing: " + string.Join("; ", errors));
        return compilation;
    }

    private static string[] Found(string body) =>
        InMeshImpersonationReferences.Find(Compile(body)).Select(r => r.Symbol).ToArray();

    [Theory]
    // Direct call, the shape the runtime guard also sees.
    [InlineData("public static void M(AccessService a) { using (a.ImpersonateAsSystem()) { } }",
        "MeshWeaver.Messaging.AccessService.ImpersonateAsSystem")]
    // TAIL CALL: the runtime guard loses this frame; the compiler does not.
    [InlineData("public static IDisposable M(AccessService a) => a.ImpersonateAsSystem();",
        "MeshWeaver.Messaging.AccessService.ImpersonateAsSystem")]
    // DELEGATE HAND-BACK: a method group returned to a platform subscriber.
    [InlineData("public static IObservable<int> M(AccessService a) => Observable.Using(a.ImpersonateAsSystem, _ => Observable.Return(1));",
        "MeshWeaver.Messaging.AccessService.ImpersonateAsSystem")]
    [InlineData("public static IDisposable M(AccessService a, IMessageHub h) => a.ImpersonateAsHub(h);",
        "MeshWeaver.Messaging.AccessService.ImpersonateAsHub")]
    [InlineData("public static IObservable<int> M(AccessService a) => a.RunAsSystem(() => Observable.Return(1));",
        "MeshWeaver.Messaging.ImpersonationScopeExtensions.RunAsSystem")]
    [InlineData("public static IDisposable M(AccessService a) => AccessContextScope.AsSystem(a);",
        "MeshWeaver.Mesh.Security.AccessContextScope.AsSystem")]
    // Hand-built contexts and the APIs that install them without a surface.
    [InlineData("public static AccessContext M() => WellKnownUsers.SystemContext;",
        "MeshWeaver.Mesh.Security.WellKnownUsers.SystemContext")]
    [InlineData("public static AccessContext M() => new() { ObjectId = WellKnownUsers.System };",
        "MeshWeaver.Mesh.Security.WellKnownUsers.System")]
    [InlineData("public static AccessContext M() => new() { ObjectId = \"system-security\" };",
        "\"system-security\"")]
    [InlineData("public static AccessContext M() => new() { ObjectId = \"node/x\", IsHub = true };",
        "MeshWeaver.Messaging.AccessContext.IsHub (written)")]
    [InlineData("public static IMessageDelivery M(IMessageDelivery d, AccessContext c) => d.SetAccessContext(c);",
        "MeshWeaver.Messaging.IMessageDelivery.SetAccessContext")]
    [InlineData("public static IMessageDelivery M(IMessageHub h, IMessageDelivery d) => h.DeliverMessage(d);",
        "MeshWeaver.Messaging.IMessageHub.DeliverMessage")]
    // A query as somebody else: System, any viewer, or a written UserId (initializer and `with`).
    [InlineData("public static MeshQueryRequest M(MeshQueryRequest r) => r.AsSystem();",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.AsSystem")]
    [InlineData("public static MeshQueryRequest M(MeshQueryRequest r) => r with { UserId = \"alice\" };",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.UserId (written)")]
    [InlineData("public static MeshQueryRequest M() => new() { UserId = \"alice\" };",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.UserId (written)")]
    public void AnImpersonationReference_IsFound(string body, string expected)
    {
        Assert.Contains(expected, Found(body));
    }

    [Fact]
    public void OrdinaryIdentityCode_IsNotReported()
    {
        // NEGATIVE CONTROL — every line here touches the same types without escalating.
        var found = Found("""
            public static void M(AccessService a, AccessContext viewer, MeshQueryRequest r, AccessContext c)
            {
                using (a.SwitchAccessContext(viewer)) { }
                var who = a.Context?.ObjectId;
                var asked = r.UserId;
                var isHub = c.IsHub;
                var text = "system";
            }
            """);

        Assert.Empty(found);
    }

    [Fact]
    public void TheLocation_NamesTheLine()
    {
        var reference = InMeshImpersonationReferences.Find(Compile(
            "public static IDisposable M(AccessService a)\n=> a.ImpersonateAsSystem();")).Single();

        Assert.Contains("Probe.cs(", reference.Location);
    }
}
