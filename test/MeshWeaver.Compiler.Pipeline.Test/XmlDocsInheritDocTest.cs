using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <c>&lt;inheritdoc/&gt;</c> resolution in <see cref="XmlDocsReader"/>, over an assembly compiled
/// with XML documentation for exactly these shapes. One fact per review finding on #7178; each says
/// what it should fail on.
/// </summary>
public sealed class XmlDocsInheritDocTest : IDisposable
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const string Source = """
        namespace Inherit;

        public interface IA
        {
            /// <summary>IA name.</summary>
            string Name { get; }
            /// <summary>IA changed.</summary>
            event System.EventHandler Changed;
        }

        public interface IB
        {
            /// <summary>IB name.</summary>
            string Name { get; }
            /// <summary>IB changed.</summary>
            event System.EventHandler Changed;
        }

        public sealed class Both : IA, IB
        {
            /// <inheritdoc/>
            string IA.Name => "";
            /// <inheritdoc/>
            string IB.Name => "";
            /// <inheritdoc/>
            event System.EventHandler IA.Changed { add { } remove { } }
            /// <inheritdoc/>
            event System.EventHandler IB.Changed { add { } remove { } }
        }

        public sealed class Cycle
        {
            /// <inheritdoc cref="Second"/>
            public int First;
            /// <inheritdoc cref="First"/>
            public int Second;
        }

        public abstract class ArityBase
        {
            /// <summary>Non-generic M.</summary>
            public virtual void M() { }
            /// <summary>Generic M.</summary>
            public virtual void M<T>() { }
        }

        public sealed class ArityDerived : ArityBase
        {
            /// <inheritdoc/>
            public override void M<T>() { }
            /// <inheritdoc/>
            public override void M() { }
        }

        public interface IFloor
        {
            /// <summary>Raises the floor.</summary>
            void Raise(int value);
        }

        public sealed class Open<T> : IFloor
        {
            /// <inheritdoc/>
            void IFloor.Raise(int value) { }
        }

        public interface IService
        {
            /// <summary>Service summary.</summary>
            void Run();
        }

        public class Impl : IService
        {
            /// <inheritdoc/>
            public void Run() { }
        }

        public sealed class Alias
        {
            /// <inheritdoc cref="Impl.Run"/>
            public void Go() { }
        }
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "xmldocs-inherit-" + Guid.NewGuid().ToString("N"));
    private readonly XmlDocsReader docs = MessageHubExtensions.CreateTypeRegistry().XmlDocs();
    private readonly Assembly assembly;
    private readonly AssemblyLoadContext context;

    public XmlDocsInheritDocTest()
        => assembly = XmlDocsFixtureAssembly.Emit(root, "Inherit", Source, out context);

    public void Dispose()
    {
        context.Unload();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private Type T(string name) => assembly.GetType("Inherit." + name, throwOnError: true)!;

    /// <summary>
    /// 4370427404 — <b>Should fail if</b> a cref hop bypasses the depth bound: the two fields'
    /// crefs point at each other and the lookup recurses until the test host dies of a stack overflow.
    /// </summary>
    [Fact]
    public void ACrefCycle_EndsAtTheDepthBound_AndReadsEmpty()
    {
        docs.Summary(T("Cycle").GetField("First")!).Should().BeEmpty();
        docs.Summary(T("Cycle").GetField("Second")!).Should().BeEmpty();
    }

    /// <summary>
    /// 4370427516 — <b>Should fail if</b> generic arity is not part of the signature: the generic
    /// override inherits the NON-generic base overload's summary (declared first).
    /// </summary>
    [Fact]
    public void AGenericOverride_InheritsTheGenericOverload_NotTheNonGenericOne()
    {
        var methods = T("ArityDerived").GetMethods(Any).Where(m => m.Name == "M").ToArray();
        docs.Summary(methods.Single(m => m.IsGenericMethodDefinition)).Should().Be("Generic M.");
        docs.Summary(methods.Single(m => !m.IsGenericMethodDefinition)).Should().Be("Non-generic M.");
    }

    /// <summary>
    /// 4370427481 — <b>Should fail if</b> an open generic declaring type (no interface map) skips
    /// the name fallback: the explicit implementation reads empty.
    /// </summary>
    [Fact]
    public void AnExplicitImplementationOnAnOpenGeneric_InheritsTheInterfaceSummary()
    {
        var raise = T("Open`1").GetMethods(Any).Single(m => m.Name.EndsWith(".Raise", StringComparison.Ordinal));
        docs.Summary(raise).Should().Be("Raises the floor.");
    }

    /// <summary>
    /// 4370427501 — <b>Should fail if</b> properties or events are matched by unqualified name: both
    /// explicit implementations read the FIRST interface's summary.
    /// </summary>
    [Fact]
    public void ExplicitPropertiesAndEvents_InheritFromTheInterfaceTheyImplement()
    {
        var both = T("Both");
        docs.Summary(both.GetProperty("Inherit.IA.Name", Any)!).Should().Be("IA name.");
        docs.Summary(both.GetProperty("Inherit.IB.Name", Any)!).Should().Be("IB name.");
        docs.Summary(both.GetEvent("Inherit.IA.Changed", Any)!).Should().Be("IA changed.");
        docs.Summary(both.GetEvent("Inherit.IB.Changed", Any)!).Should().Be("IB changed.");
    }

    /// <summary>
    /// 4370427530 — <b>Should fail if</b> a cref to a member whose own <c>&lt;inheritdoc/&gt;</c> has no
    /// cref stops there: the alias reads empty while the target reads the interface summary.
    /// </summary>
    [Fact]
    public void ACrefToAnInheritingMember_ContinuesTheInheritance()
    {
        docs.Summary(T("Impl").GetMethod("Run")!).Should().Be("Service summary.", "CONTROL — the target read directly");
        docs.Summary(T("Alias").GetMethod("Go")!).Should().Be("Service summary.");
    }
}
