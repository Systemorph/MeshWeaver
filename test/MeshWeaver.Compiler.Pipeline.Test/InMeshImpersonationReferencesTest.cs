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
        """;

    private static Compilation Compile(string body) =>
        CompileSource($"{Usings}\npublic static class Probe {{\n{body}\n}}");

    private static CSharpCompilation CompileSource(string source, string? extraTree = null)
    {
        // Touch every assembly the snippets bind against so it is loaded and has a Location.
        _ = new[] { typeof(AccessService), typeof(WellKnownUsers), typeof(MeshQueryRequest),
                    typeof(ContentImportBuilder), typeof(System.Reactive.Linq.Observable),
                    typeof(System.Text.Json.JsonSerializerOptions), typeof(MessageDelivery<>),
                    typeof(MeshWeaver.Data.Serialization.SynchronizationStream<>),
                    typeof(MeshWeaver.Hosting.Persistence.MarkdownBodyKeyRepair) };
        // 🚨 Only assemblies whose file is STILL on disk. Other classes of this project compile
        // node assemblies into temp folders, load them and delete the folder when they finish; such
        // an assembly stays in the AppDomain with a Location that no longer exists, and referencing
        // it fails the compilation with FileNotFoundException. Which classes ran before this one is
        // a matter of class order, so the reference set must not depend on it.
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && System.IO.File.Exists(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create("InMeshProbe",
            extraTree is null
                ? [CSharpSyntaxTree.ParseText(source, path: "Probe.cs")]
                : [CSharpSyntaxTree.ParseText(source, path: "Probe.cs"), CSharpSyntaxTree.ParseText(extraTree, path: "ModuleMailCopyStubs.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "the probe must bind, or the scan proves nothing: " + string.Join("; ", errors));
        return compilation;
    }

    private static string[] Found(string body) =>
        InMeshImpersonationReferences.Find(Compile(body)).Select(r => r.Symbol).ToArray();

    /// <summary>
    /// 🚨 STUBS of the mail copy's entry points under the namespace they carry in the Mail MODULE
    /// (MeshWeaver.Plugins, <c>MeshWeaver.Mail.MailCopy</c>). That module is not referenceable from core, and the
    /// table is keyed <c>Namespace.TypeName</c> with no assembly — so a same-named stub is an honest stand-in for
    /// proving the table FIRES on those names. Signatures mirror the module's. That the table is COMPLETE against the
    /// real moved types is checked where both are visible: MeshWeaver.Plugins' <c>MailCopySurfaceRegistrationTest</c>.
    /// </summary>
    private const string ModuleMailCopyStubs = """
        using System;
        using System.Reactive;
        namespace MeshWeaver.Mail.MailCopy
        {
            public sealed class MailMessageCopy { }
            public sealed class MailCopySource { }
            public interface IMailCopyStore
            {
                IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy);
                IObservable<MailMessageCopy> Read(string owner, string provider, string generation, string messageId);
            }
            public sealed class MeshMailCopyStore : IMailCopyStore
            {
                public IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy) => throw new NotImplementedException();
                public IObservable<MailMessageCopy> Read(string owner, string provider, string generation, string messageId) => throw new NotImplementedException();
            }
            public interface IMailAttachmentStore
            {
                IObservable<Unit> DeleteAll(string owner, string copyPath);
                IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes);
            }
            public sealed class MeshMailAttachmentStore : IMailAttachmentStore
            {
                public IObservable<Unit> DeleteAll(string owner, string copyPath) => throw new NotImplementedException();
                public IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes) => throw new NotImplementedException();
            }
            public sealed class MailCopyEngine
            {
                public void Kick(string owner, bool force = false) => throw new NotImplementedException();
                public IObservable<MailCopySource> Sync(string owner) => throw new NotImplementedException();
                public IObservable<MailCopySource> Enable(string owner, int windowDays) => throw new NotImplementedException();
            }
            public interface IMailAccountCopy
            {
                void KickAll(string owner);
                IObservable<bool> Enable(string owner, string accountId, int windowDays);
            }
            public sealed class MailFeedAccountCopy : IMailAccountCopy
            {
                public void KickAll(string owner) => throw new NotImplementedException();
                public IObservable<bool> Enable(string owner, string accountId, int windowDays) => throw new NotImplementedException();
            }
            public interface IMailCopySwitch
            {
                IObservable<bool> Enable(string owner, string provider, string accountId);
            }
            public sealed class MailCopySwitch : IMailCopySwitch
            {
                public IObservable<bool> Enable(string owner, string provider, string accountId) => throw new NotImplementedException();
            }
        }
        """;

    // The probe with the module stubs as a second tree. The probe names the stubs FULLY QUALIFIED, as in-mesh code that
    // references the Mail module may.
    private static string[] FoundWithModuleStubs(string body)
    {
        var compilation = CompileSource($"{Usings}\npublic static class Probe {{\n{body}\n}}", ModuleMailCopyStubs);
        return InMeshImpersonationReferences.Find(compilation).Select(r => r.Symbol).ToArray();
    }

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
    // The storage repair sweeps WRITE AS SYSTEM over any partition the caller names; in-mesh code
    // reaching one is an escalation (review on #7645). Detect only reads and is not reported.
    [InlineData("public static object M(IMessageHub h) => MeshWeaver.Hosting.Persistence.MarkdownBodyKeyRepair.Repair(h, new[] { \"someone-else\" });",
        "MeshWeaver.Hosting.Persistence.MarkdownBodyKeyRepair.Repair")]
    [InlineData("public static object M(IMessageHub h) => MeshWeaver.Hosting.Persistence.StaleMainNodeRepair.Repair(h);",
        "MeshWeaver.Hosting.Persistence.StaleMainNodeRepair.Repair")]
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
    /// The SAME surfaces under the namespace they carry in the Mail module, registered before the module ships them, so
    /// no window exists in which a method group handed to a platform callback reaches a moved System entry point
    /// unreported. Bound against <see cref="ModuleMailCopyStubs"/>.
    /// </summary>
    [Theory]
    [InlineData("public static IObservable<MeshWeaver.Mail.MailCopy.MailMessageCopy> M(MeshWeaver.Mail.MailCopy.IMailCopyStore s, MeshWeaver.Mail.MailCopy.MailMessageCopy c) => s.Upsert(\"owner\", c);",
        "MeshWeaver.Mail.MailCopy.IMailCopyStore.Upsert")]
    [InlineData("public static IObservable<MeshWeaver.Mail.MailCopy.MailMessageCopy> M(MeshWeaver.Mail.MailCopy.MeshMailCopyStore s, MeshWeaver.Mail.MailCopy.MailMessageCopy c) => s.Upsert(\"owner\", c);",
        "MeshWeaver.Mail.MailCopy.MeshMailCopyStore.Upsert")]
    // DEFERRED: a method group handed on, invoked later by whoever holds it — the shape only this check sees.
    [InlineData("public static Func<string, MeshWeaver.Mail.MailCopy.MailMessageCopy, IObservable<MeshWeaver.Mail.MailCopy.MailMessageCopy>> M(MeshWeaver.Mail.MailCopy.IMailCopyStore s) => s.Upsert;",
        "MeshWeaver.Mail.MailCopy.IMailCopyStore.Upsert")]
    [InlineData("public static Func<string, IObservable<MeshWeaver.Mail.MailCopy.MailMessageCopy>> M(MeshWeaver.Mail.MailCopy.MeshMailCopyStore s) => o => s.Read(o, \"outlook\", \"g1\", \"m1\");",
        "MeshWeaver.Mail.MailCopy.MeshMailCopyStore.Read")]
    [InlineData("public static Func<string, string, IObservable<System.Reactive.Unit>> M(MeshWeaver.Mail.MailCopy.IMailAttachmentStore s) => s.DeleteAll;",
        "MeshWeaver.Mail.MailCopy.IMailAttachmentStore.DeleteAll")]
    [InlineData("public static IObservable<string> M(MeshWeaver.Mail.MailCopy.MeshMailAttachmentStore s, byte[] b) => s.Save(\"owner\", \"p\", \"r\", b);",
        "MeshWeaver.Mail.MailCopy.MeshMailAttachmentStore.Save")]
    [InlineData("public static Action<string, bool> M(MeshWeaver.Mail.MailCopy.MailCopyEngine e) => e.Kick;",
        "MeshWeaver.Mail.MailCopy.MailCopyEngine.Kick")]
    [InlineData("public static Func<string, IObservable<MeshWeaver.Mail.MailCopy.MailCopySource>> M(MeshWeaver.Mail.MailCopy.MailCopyEngine e) => e.Sync;",
        "MeshWeaver.Mail.MailCopy.MailCopyEngine.Sync")]
    [InlineData("public static IObservable<MeshWeaver.Mail.MailCopy.MailCopySource> M(MeshWeaver.Mail.MailCopy.MailCopyEngine e) => e.Enable(\"owner\", 30);",
        "MeshWeaver.Mail.MailCopy.MailCopyEngine.Enable")]
    [InlineData("public static Action<string> M(MeshWeaver.Mail.MailCopy.IMailAccountCopy a) => a.KickAll;",
        "MeshWeaver.Mail.MailCopy.IMailAccountCopy.KickAll")]
    [InlineData("public static void M(MeshWeaver.Mail.MailCopy.MailFeedAccountCopy a) => a.KickAll(\"owner\");",
        "MeshWeaver.Mail.MailCopy.MailFeedAccountCopy.KickAll")]
    [InlineData("public static IObservable<bool> M(MeshWeaver.Mail.MailCopy.MailFeedAccountCopy a) => a.Enable(\"owner\", \"acct\", 30);",
        "MeshWeaver.Mail.MailCopy.MailFeedAccountCopy.Enable")]
    [InlineData("public static Func<string, string, int, IObservable<bool>> M(MeshWeaver.Mail.MailCopy.IMailAccountCopy a) => a.Enable;",
        "MeshWeaver.Mail.MailCopy.IMailAccountCopy.Enable")]
    [InlineData("public static IObservable<bool> M(MeshWeaver.Mail.MailCopy.IMailCopySwitch s) => s.Enable(\"owner\", \"outlook\", \"acct\");",
        "MeshWeaver.Mail.MailCopy.IMailCopySwitch.Enable")]
    [InlineData("public static IObservable<bool> M(MeshWeaver.Mail.MailCopy.MailCopySwitch s) => s.Enable(\"owner\", \"outlook\", \"acct\");",
        "MeshWeaver.Mail.MailCopy.MailCopySwitch.Enable")]
    public void AModuleMailCopyReference_IsFound(string body, string expected)
    {
        Assert.Contains(expected, FoundWithModuleStubs(body));
    }

    /// <summary>
    /// NEGATIVE CONTROL for the module names: the stubs themselves and code that merely holds the types report
    /// nothing, so a pass above is the references, not the tree they were bound against.
    /// </summary>
    [Fact]
    public void ModuleMailCopyTypes_Untouched_AreNotReported()
    {
        Assert.Empty(FoundWithModuleStubs($"public static object M(MeshWeaver.Mail.MailCopy.IMailCopyStore s, MeshWeaver.Mail.MailCopy.MailCopyEngine e) => (s, e);"));
    }

    /// <summary>Negative control for the repair rows: the read-only <c>Detect</c> half writes nothing and is not reported.</summary>
    [Fact]
    public void TheReadOnlyRepairDetect_IsNotReported()
    {
        Assert.Empty(Found("public static object M(IMessageHub h) => MeshWeaver.Hosting.Persistence.MarkdownBodyKeyRepair.Detect(h);"));
        Assert.Empty(Found("public static object M(IMessageHub h) => MeshWeaver.Hosting.Persistence.StaleMainNodeRepair.Detect(h);"));
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
            authored, [generatorPath], new SourceGeneratorLedger(), "Test/Probe",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
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
