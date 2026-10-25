using System;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

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
        using MeshWeaver.Graph.MailCopy;
        """;

    private static Compilation Compile(string body) =>
        CompileSource($"{Usings}\npublic static class Probe {{\n{body}\n}}");

    private static CSharpCompilation CompileSource(string source)
    {
        // Touch every assembly the snippets bind against so it is loaded and has a Location.
        _ = new[] { typeof(AccessService), typeof(WellKnownUsers), typeof(MeshQueryRequest),
                    typeof(ContentImportBuilder), typeof(System.Reactive.Linq.Observable),
                    typeof(System.Text.Json.JsonSerializerOptions), typeof(MessageDelivery<>),
                    typeof(MeshWeaver.Data.Serialization.SynchronizationStream<>),
                    typeof(MeshWeaver.Graph.MailCopy.MeshMailCopyStore) };
        // 🚨 Only assemblies whose file is STILL on disk. Other classes of this project compile
        // node assemblies into temp folders, load them and delete the folder when they finish; such
        // an assembly stays in the AppDomain with a Location that no longer exists, and referencing
        // it fails the compilation with FileNotFoundException. Which classes ran before this one is
        // a matter of class order, so the reference set must not depend on it.
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && System.IO.File.Exists(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create("InMeshProbe",
            [CSharpSyntaxTree.ParseText(source, path: "Probe.cs")],
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
    // The STATEMENT-shaped write: the member is the Name of a member access that is the left side
    // of an assignment (`this.UserId = …`). UserId is init-only, so a derived request's constructor
    // is where in-mesh code can write it that way.
    [InlineData("public record Mine : MeshQueryRequest { public Mine(string who) { this.UserId = who; } }",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.UserId (written)")]
    // The factories that install a query identity with NO written member — positional and named.
    [InlineData("public static MeshQueryRequest M(string who) => MeshQueryRequest.FromQuery(\"nodeType:X\", who);",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.FromQuery (userId supplied)")]
    [InlineData("public static MeshQueryRequest M(string who) => MeshQueryRequest.FromQuery(\"nodeType:X\", who, \"p\");",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.FromQuery (userId supplied)")]
    [InlineData("public static MeshQueryRequest M(string who) => MeshQueryRequest.FromQueries(new[] { \"a\" }, userId: who);",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.FromQueries (userId supplied)")]
    // A method group has no argument list to read: whoever invokes it decides, so it is reported.
    [InlineData("public static Func<string, string?, MeshQueryRequest> M() => MeshQueryRequest.FromQuery;",
        "MeshWeaver.Mesh.Services.MeshQueryRequest.FromQuery (userId supplied)")]
    // A fully qualified expression is a member access, not a qualified NAME, and is still bound.
    [InlineData("public static string M() => MeshWeaver.Mesh.Security.WellKnownUsers.System;",
        "MeshWeaver.Mesh.Security.WellKnownUsers.System")]
    // 🚨 DELIBERATE: the literal is reported wherever it stands — a comparison too. The scan is
    // syntactic and cannot follow a value from a local into an identity (`var id = "system-security";
    // new AccessContext { ObjectId = id }`), so narrowing it to "written to an identity member"
    // would lose exactly that forgery. What LogOnly records is therefore an inventory, and the trust
    // list — not the scan — is what separates a comparison from a forgery at the flip.
    [InlineData("public static bool M(AccessContext c) => c.ObjectId == \"system-security\";",
        "\"system-security\"")]
    [InlineData("public static AccessContext M() { var id = \"system-security\"; return new() { ObjectId = id }; }",
        "\"system-security\"")]
    public void AnImpersonationReference_IsFound(string body, string expected)
    {
        Assert.Contains(expected, Found(body));
    }

    /// <summary>
    /// #6619: a delivery envelope in-mesh code builds itself names its own sender, and nothing
    /// stamps it — only <c>hub.Post</c> does (Doc/Architecture/SenderIsThePostingHub). Every way to
    /// MINT such an envelope and every entry point that ROUTES one without <c>hub.Post</c> is
    /// reported, so under <c>Enforce</c> the compile is refused and the forged sender never runs.
    /// </summary>
    [Theory]
    // Minting: the constructor that takes a sender, the one that takes caller-written PostOptions,
    // a target-typed `new(…)`, a sender rewritten on an envelope, and a derived envelope.
    [InlineData("public static IMessageDelivery M(Address forged, Address target) => new MessageDelivery<string>(forged, target, \"x\", new System.Text.Json.JsonSerializerOptions());",
        "MeshWeaver.Messaging.MessageDelivery..ctor")]
    [InlineData("public static IMessageDelivery M(Address forged, Address target) => new MessageDelivery<string>(\"x\", new PostOptions(forged).WithTarget(target), new System.Text.Json.JsonSerializerOptions());",
        "MeshWeaver.Messaging.MessageDelivery..ctor")]
    [InlineData("public static MessageDelivery<string> M(Address forged, Address target) => new(forged, target, \"x\", new System.Text.Json.JsonSerializerOptions());",
        "MeshWeaver.Messaging.MessageDelivery..ctor")]
    [InlineData("public static MessageDelivery<string> M(MessageDelivery<string> d, Address forged) => d with { Sender = forged };",
        "MeshWeaver.Messaging.MessageDelivery.Sender (written)")]
    [InlineData("public static MessageDelivery<string> M(Address forged) => new() { Sender = forged };",
        "MeshWeaver.Messaging.MessageDelivery.Sender (written)")]
    // Routing without hub.Post.
    [InlineData("public static IObservable<IMessageDelivery> M(IRoutingService r, IMessageDelivery d) => r.DeliverMessage(d);",
        "MeshWeaver.Mesh.Services.IRoutingService.DeliverMessage")]
    // The public concrete stream's DeliverMessage forwards a caller-built envelope with its sender.
    [InlineData("public static IMessageDelivery M(MeshWeaver.Data.Serialization.SynchronizationStream<object> s, IMessageDelivery d) => s.DeliverMessage(d);",
        "MeshWeaver.Data.Serialization.SynchronizationStream.DeliverMessage")]
    public void AForgedSenderEnvelope_IsFound(string body, string expected)
    {
        Assert.Contains(expected, Found(body));
    }

    /// <summary>
    /// The mail copy's stores (and the engine's background sync) run as System on the owner's behalf. Their
    /// runtime check judges the immediate caller, which a method group handed to a later platform callback
    /// replaces — so every reference, a DEFERRED method group included, is reported at compile time, on the
    /// interface as on the concrete store.
    /// </summary>
    [Theory]
    [InlineData("public static IObservable<MailMessageCopy> M(IMailCopyStore s, MailMessageCopy c) => s.Upsert(\"owner\", c);",
        "MeshWeaver.Graph.MailCopy.IMailCopyStore.Upsert")]
    [InlineData("public static IObservable<MailMessageCopy> M(MeshMailCopyStore s, MailMessageCopy c) => s.Upsert(\"owner\", c);",
        "MeshWeaver.Graph.MailCopy.MeshMailCopyStore.Upsert")]
    // DEFERRED: a method group handed on, invoked later by whoever holds it.
    [InlineData("public static Func<string, MailMessageCopy, IObservable<MailMessageCopy>> M(IMailCopyStore s) => s.Upsert;",
        "MeshWeaver.Graph.MailCopy.IMailCopyStore.Upsert")]
    [InlineData("public static Func<string, IObservable<MailMessageCopy?>> M(MeshMailCopyStore s) => o => s.Read(o, \"outlook\", \"g1\", \"m1\");",
        "MeshWeaver.Graph.MailCopy.MeshMailCopyStore.Read")]
    [InlineData("public static Func<string, string, IObservable<System.Reactive.Unit>> M(IMailAttachmentStore s) => s.DeleteAll;",
        "MeshWeaver.Graph.MailCopy.IMailAttachmentStore.DeleteAll")]
    [InlineData("public static IObservable<string> M(MeshMailAttachmentStore s, byte[] b) => s.Save(\"owner\", \"p\", \"r\", b);",
        "MeshWeaver.Graph.MailCopy.MeshMailAttachmentStore.Save")]
    [InlineData("public static Action<string, bool> M(MailCopyEngine e) => e.Kick;",
        "MeshWeaver.Graph.MailCopy.MailCopyEngine.Kick")]
    [InlineData("public static Func<string, IObservable<MailCopySource?>> M(MailCopyEngine e) => e.Sync;",
        "MeshWeaver.Graph.MailCopy.MailCopyEngine.Sync")]
    [InlineData("public static Action<string> M(IMailAccountCopy a) => a.KickAll;",
        "MeshWeaver.Graph.MailCopy.IMailAccountCopy.KickAll")]
    [InlineData("public static void M(MailFeedAccountCopy a) => a.KickAll(\"owner\");",
        "MeshWeaver.Graph.MailCopy.MailFeedAccountCopy.KickAll")]
    // Enable starts the System backfill: every public road to it.
    [InlineData("public static IObservable<MailCopySource> M(MailCopyEngine e) => e.Enable(\"owner\", 30);",
        "MeshWeaver.Graph.MailCopy.MailCopyEngine.Enable")]
    [InlineData("public static IObservable<bool> M(MailFeedAccountCopy a) => a.Enable(\"owner\", \"acct\", 30);",
        "MeshWeaver.Graph.MailCopy.MailFeedAccountCopy.Enable")]
    [InlineData("public static Func<string, string, int, IObservable<bool>> M(IMailAccountCopy a) => a.Enable;",
        "MeshWeaver.Graph.MailCopy.IMailAccountCopy.Enable")]
    [InlineData("public static IObservable<bool> M(IMailCopySwitch s) => s.Enable(\"owner\", \"outlook\", \"acct\");",
        "MeshWeaver.Graph.MailCopy.IMailCopySwitch.Enable")]
    [InlineData("public static IObservable<bool> M(MailCopySwitch s) => s.Enable(\"owner\", \"outlook\", \"acct\");",
        "MeshWeaver.Graph.MailCopy.MailCopySwitch.Enable")]
    public void AMailCopyStoreReference_IsFound(string body, string expected)
    {
        Assert.Contains(expected, Found(body));
    }

    /// <summary>
    /// The registration above is a list, and a list drifts: every <c>[ImpersonationSurface]</c> of the mail
    /// copy's types — and the interface member each one implements — must be in it, or a new System entry
    /// point would be reachable from in-mesh code unreported.
    /// </summary>
    [Fact]
    public void EveryMailCopySurface_IsRegistered()
    {
        var types = typeof(MeshMailCopyStore).Assembly.GetTypes()
            .Where(t => t.Namespace == "MeshWeaver.Graph.MailCopy" && t.IsPublic && !t.IsInterface)
            .ToArray();
        var surfaces = types.SelectMany(t => t
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.IsDefined(typeof(ImpersonationSurfaceAttribute), inherit: false))
                .SelectMany(m => new[] { $"{t.Namespace}.{t.Name}.{m.Name}" }
                    .Concat(t.GetInterfaces()
                        .Where(i => i.Namespace == t.Namespace)
                        .Where(i => t.GetInterfaceMap(i).TargetMethods.Contains(m))
                        .Select(i => $"{i.Namespace}.{i.Name}.{m.Name}"))))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(surfaces);
        var missing = surfaces.Where(s =>
        {
            var cut = s.LastIndexOf('.');
            return !(InMeshImpersonationReferences.Members.TryGetValue(s[..cut], out var members) && members.Contains(s[(cut + 1)..]));
        }).ToArray();
        Assert.True(missing.Length == 0, "unregistered System surfaces: " + string.Join(", ", missing));
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
                // The factories WITHOUT a user id install no identity.
                var plain = MeshQueryRequest.FromQuery("nodeType:X");
                var union = MeshQueryRequest.FromQueries(new[] { "a", "b" });
                // `System` in a namespace-or-type position is never the WellKnownUsers member.
                System.Collections.Generic.List<int> numbers = new();
                var now = System.DateTimeOffset.UtcNow;
            }
            """);

        Assert.Empty(found);
    }

    [Fact]
    public void OrdinaryMessaging_IsNotReported()
    {
        // NEGATIVE CONTROL for #6619 — posting through the hub (which stamps the sender), READING a
        // delivery's sender, a caller-written PostOptions sender (overwritten at Post), retargeting a
        // received delivery, an empty envelope and an unrelated `new(…)` must produce nothing.
        var found = Found("""
            public sealed record Ping(string A, string B, string C);
            public static void M(IMessageHub h, IMessageDelivery d, Address target)
            {
                h.Post(new Ping("a", "b", "c"), o => o.WithTarget(target));
                h.Post(new Ping("a", "b", "c"), o => o.WithTarget(target) with { Sender = target });
                var from = d.Sender;
                var onward = d.ForwardTo(target);
                var empty = new MessageDelivery<string>();
                Ping ping = new("a", "b", "c");
            }
            """);

        Assert.Empty(found);
    }

    /// <summary>
    /// A chained script session judges each cell for ITS OWN references: a continued submission's
    /// compilation holds one syntax tree — the new cell's — and earlier cells are a referenced
    /// previous compilation. So a clean cell after an escalating one reports nothing, and the
    /// escalating one is named once, at the cell that wrote it. Measured through the same
    /// <see cref="MeshWeaver.Kernel.Hub.ScriptSession"/> the kernel compiles cells with.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ALaterCell_IsNotJudgedForAnEarlierCellsReference()
    {
        var options = Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
            .AddReferences(typeof(WellKnownUsers).Assembly, typeof(AccessContext).Assembly)
            .AddImports("MeshWeaver.Mesh.Security");
        using var session = new MeshWeaver.Kernel.Hub.ScriptSession(new object());
        var ct = TestContext.Current.CancellationToken;

        var first = session.Compile("var who = WellKnownUsers.System;", options, typeof(object), ct);
        Assert.Equal(["MeshWeaver.Mesh.Security.WellKnownUsers.System"],
            InMeshImpersonationReferences.Find(first.Compilation, ct).Select(r => r.Symbol));
        await session.ExecuteAsync(first, ct);

        // The second cell READS the first cell's variable, so it really is a continuation.
        var second = session.Compile("var length = who.Length;", options, typeof(object), ct);
        Assert.Single(second.Compilation.SyntaxTrees);
        Assert.Empty(InMeshImpersonationReferences.Find(second.Compilation, ct));
    }

    [Fact]
    public void TheLocation_NamesTheLine()
    {
        var reference = InMeshImpersonationReferences.Find(Compile(
            "public static IDisposable M(AccessService a)\n=> a.ImpersonateAsSystem();")).Single();

        Assert.Contains("Probe.cs(", reference.Location);
    }

    /// <summary>
    /// A using ALIAS renames the envelope type, so a name-only prefilter would skip the constructor
    /// (#6619 review). Both a file-local and a global alias are found.
    /// </summary>
    [Theory]
    [InlineData("using D = MeshWeaver.Messaging.MessageDelivery<string>;")]
    [InlineData("global using D = MeshWeaver.Messaging.MessageDelivery<string>;")]
    public void AnAliasedEnvelopeConstructor_IsFound(string alias)
    {
        var compilation = CompileSource($$"""
            {{alias}}
            {{Usings}}
            public static class Probe
            {
                public static IMessageDelivery M(Address forged, Address target) =>
                    new D(forged, target, "x", new System.Text.Json.JsonSerializerOptions());
            }
            """);

        Assert.Contains("MeshWeaver.Messaging.MessageDelivery..ctor",
            InMeshImpersonationReferences.Find(compilation).Select(r => r.Symbol));
    }

    /// <summary>
    /// A NODE-SUPPLIED source generator (one the node #r'd) runs code the node chose, so what it emits
    /// is scanned (#6619 review): <c>GeneratorPipeline.RunSourceGeneratorsWithNodeOutput</c> hands back its trees,
    /// and the scan over authored + those trees finds the forged envelope. Negative control: the
    /// authored trees alone — what the compile path scanned before — find nothing.
    /// </summary>
    [Fact]
    public void ANodeSuppliedGeneratorsForgedEnvelope_IsFound()
    {
        var generatorPath = BuildGenerator();
        var authored = CompileSource($"{Usings}\npublic static class Probe {{ }}");

        var generated = GeneratorPipeline.RunSourceGeneratorsWithNodeOutput(
            authored, [generatorPath], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TestContext.Current.CancellationToken, out var nodeGenerated);

        Assert.Contains(nodeGenerated, t => t.FilePath.EndsWith("Forged.g.cs", StringComparison.Ordinal));
        Assert.Empty(InMeshImpersonationReferences.Find(generated, authored.SyntaxTrees));
        Assert.Contains("MeshWeaver.Messaging.MessageDelivery..ctor",
            InMeshImpersonationReferences.Find(generated, authored.SyntaxTrees.Concat(nodeGenerated))
                .Select(r => r.Symbol));
    }

    // A generator assembly built here, on disk, the way a node's #r "nuget:" resolve hands one over.
    private static string BuildGenerator()
    {
        const string source = """
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class ForgeGenerator : ISourceGenerator
            {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context) =>
                    context.AddSource("Forged.g.cs",
                        "public static class Forged { public static MeshWeaver.Messaging.IMessageDelivery M(MeshWeaver.Messaging.Address a, MeshWeaver.Messaging.Address t) => new MeshWeaver.Messaging.MessageDelivery<string>(a, t, \"x\", new System.Text.Json.JsonSerializerOptions()); }");
            }
            """;
        var name = $"ForgeGenerator_{Guid.NewGuid():N}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Append(typeof(ISourceGenerator).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var directory = System.IO.Directory.CreateTempSubdirectory("forge-gen-").FullName;
        var path = System.IO.Path.Combine(directory, name + ".dll");
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, "the test generator must build: "
            + string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }
}
