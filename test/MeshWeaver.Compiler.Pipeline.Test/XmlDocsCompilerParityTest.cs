using System.Collections.Immutable;
using System.Reflection;
using System.Xml.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The documentation IDs and the inheritance paths of <see cref="XmlDocsReader"/>, checked against
/// what the C# COMPILER emits — never against IDs we wrote down ourselves. The fixture is compiled with
/// Roslyn and XML documentation in the test, so every expected ID comes from the compiler.
/// </summary>
public sealed class XmlDocsCompilerParityTest : IDisposable
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every member shape a documentation ID encodes, each documented.</summary>
    private const string Shapes = """
        using System;
        using System.Collections.Generic;
        namespace Shapes;

        /// <summary>Global.</summary>
        public delegate void Callback(int value);

        /// <summary>Enum.</summary>
        public enum Color
        {
            /// <summary>Red.</summary>
            Red,
        }

        /// <summary>IPair.</summary>
        public interface IPair<TA, TB>
        {
            /// <summary>IPair.Add.</summary>
            void Add(TA a, TB b);
            /// <summary>IPair.Item.</summary>
            TB this[TA key] { get; }
        }

        /// <summary>Holder.</summary>
        public unsafe class Holder<T> : IPair<T, string>, IDictionary<int, T>, IComparable
        {
            /// <summary>Static ctor.</summary>
            static Holder() { }
            /// <summary>Ctor.</summary>
            public Holder() { }
            /// <summary>Ctor with args.</summary>
            public Holder(T value, ref int count, out string name, in long id) { name = ""; }

            /// <summary>Field.</summary>
            public int Field;
            /// <summary>Const.</summary>
            public const string Constant = "c";
            /// <summary>Event.</summary>
            public event EventHandler<T>? Changed;
            /// <summary>Property.</summary>
            public T? Value { get; init; }
            /// <summary>Indexer.</summary>
            public int this[string key, int index] => index;

            /// <summary>Arrays.</summary>
            public void Arrays(int[] a, int[,] b, int[][] c, T[] d, List<T>[] e) { }
            /// <summary>Pointers.</summary>
            public void Pointers(int* p, void* q, int** r) { }
            /// <summary>Nullable and tuples.</summary>
            public void Shapes(int? a, (int, string) b, Dictionary<string, List<(T, int)>> c, dynamic d, params object[] rest) { }
            /// <summary>Generic method.</summary>
            public TOut Map<TOut, TOther>(T input, Func<T, TOut> f, IEnumerable<TOther> others, ref TOut seed) => seed;
            /// <summary>Nested generic argument.</summary>
            public void Take(Inner<int>.Deep<T> deep, Inner<T> inner) { }

            /// <summary>Addition.</summary>
            public static Holder<T> operator +(Holder<T> a, Holder<T> b) => a;
            /// <summary>Implicit.</summary>
            public static implicit operator T?(Holder<T> h) => h.Value;
            /// <summary>Explicit.</summary>
            public static explicit operator Holder<T>(T v) => new();
            /// <summary>Equality.</summary>
            public static bool operator ==(Holder<T> a, Holder<T> b) => true;
            /// <summary>Inequality.</summary>
            public static bool operator !=(Holder<T> a, Holder<T> b) => false;

            /// <summary>Explicit IPair.Add.</summary>
            void IPair<T, string>.Add(T a, string b) { }
            /// <summary>Explicit IPair indexer.</summary>
            string IPair<T, string>.this[T key] => "";
            /// <summary>Explicit IComparable.</summary>
            int IComparable.CompareTo(object? other) => 0;

            /// <summary>Inner.</summary>
            public class Inner<U>
            {
                /// <summary>Deep.</summary>
                public class Deep<V>
                {
                    /// <summary>Deep.Use.</summary>
                    public void Use(T t, U u, V v) { }
                }
            }

            /// <summary>IDictionary explicit.</summary>
            void IDictionary<int, T>.Add(int key, T value) { }
            /// <summary>IDictionary members.</summary>
            public bool ContainsKey(int key) => false;
            /// <summary>Remove.</summary>
            public bool Remove(int key) => false;
            /// <summary>TryGetValue.</summary>
            public bool TryGetValue(int key, out T value) { value = default!; return false; }
            /// <summary>Item.</summary>
            T IDictionary<int, T>.this[int key] { get => default!; set { } }
            /// <summary>Keys.</summary>
            public ICollection<int> Keys => Array.Empty<int>();
            /// <summary>Values.</summary>
            public ICollection<T> Values => Array.Empty<T>();
            /// <summary>Add pair.</summary>
            public void Add(KeyValuePair<int, T> item) { }
            /// <summary>Clear.</summary>
            public void Clear() { }
            /// <summary>Contains.</summary>
            public bool Contains(KeyValuePair<int, T> item) => false;
            /// <summary>CopyTo.</summary>
            public void CopyTo(KeyValuePair<int, T>[] array, int arrayIndex) { }
            /// <summary>Remove pair.</summary>
            public bool Remove(KeyValuePair<int, T> item) => false;
            /// <summary>Count.</summary>
            public int Count => 0;
            /// <summary>IsReadOnly.</summary>
            public bool IsReadOnly => false;
            /// <summary>Enumerator.</summary>
            public IEnumerator<KeyValuePair<int, T>> GetEnumerator() => null!;
            /// <summary>Non-generic enumerator.</summary>
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!;
        }

        /// <summary>Extensions.</summary>
        public static class Extensions
        {
            /// <summary>Extension.</summary>
            public static int Twice(this int value) => value * 2;
            /// <summary>Generic extension.</summary>
            public static T First<T>(this IEnumerable<T> source) where T : class => null!;
        }

        /// <summary>A positional record.</summary>
        /// <param name="Name">The record's name.</param>
        public sealed record Person(string Name);
        """;

    /// <summary>Every inheritance path: constructed vs definition, open vs closed, explicit vs implicit, cref, record params.</summary>
    private const string Inheritance = """
        namespace Inheritance;

        public interface IConvert
        {
            /// <summary>Converts a value.</summary>
            T Convert<T>(T value);
        }

        public sealed class Converter : IConvert
        {
            /// <inheritdoc/>
            public T Convert<T>(T value) => value;
        }

        public abstract class Base<T>
        {
            /// <summary>Base Run.</summary>
            public abstract void Run(T value);
            /// <summary>Base Value.</summary>
            public virtual T? Value => default;
        }

        public sealed class Closed : Base<int>
        {
            /// <inheritdoc/>
            public override void Run(int value) { }
            /// <inheritdoc/>
            public override int Value => 0;
        }

        public sealed class Open<T> : Base<T>
        {
            /// <inheritdoc/>
            public override void Run(T value) { }
        }

        public interface IFoo
        {
            /// <summary>IFoo name.</summary>
            string Name { get; }
        }

        public interface IFoo<T>
        {
            /// <summary>IFoo of T name.</summary>
            string Name { get; }
        }

        public class Outer<T>
        {
            public interface IA
            {
                /// <summary>IA name.</summary>
                string Name { get; }
            }

            public interface IB
            {
                /// <summary>IB name.</summary>
                string Name { get; }
            }
        }

        public sealed class Explicit<T> : IFoo, IFoo<int>, Outer<T>.IA, Outer<T>.IB
        {
            /// <inheritdoc/>
            string IFoo.Name => "";
            /// <inheritdoc/>
            string IFoo<int>.Name => "";
            /// <inheritdoc/>
            string Outer<T>.IA.Name => "";
            /// <inheritdoc/>
            string Outer<T>.IB.Name => "";
        }

        public sealed class Implicit : IFoo
        {
            /// <inheritdoc/>
            public string Name => "";
        }

        /// <summary>A record.</summary>
        /// <param name="Label">The record's label.</param>
        public sealed record Tagged(string Label);

        /// <summary>A type documenting its property only as a param, the shape the record fallback reads.</summary>
        /// <param name="Label">The manual label.</param>
        public sealed class Manual
        {
            public string Label => "";
        }

        public sealed class RecordAlias
        {
            /// <inheritdoc cref="Tagged.Label"/>
            public string Alias => "";
            /// <inheritdoc cref="Manual.Label"/>
            public string ManualAlias => "";
        }
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "xmldocs-parity-" + Guid.NewGuid().ToString("N"));
    private readonly XmlDocsReader docs = MessageHubExtensions.CreateTypeRegistry().XmlDocs();
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private Assembly Compile(string node, string source, out ImmutableHashSet<string> documentedIds)
    {
        var directory = Path.Combine(root, node);
        var assembly = XmlDocsFixtureAssembly.Emit(directory, node, source, out _);
        documentedIds = XDocument.Load(Path.Combine(directory, "DynamicNode_" + node + ".xml"))
            .Descendants("member").Select(m => (string)m.Attribute("name")!).ToImmutableHashSet();
        return assembly;
    }

    /// <summary>
    /// <b>Should fail if</b> any ID shape — nested and generic types, arrays (single, multi-dimensional,
    /// jagged), pointers, by-ref / out / in, nullable, tuples, dynamic, params, generic methods, nested
    /// generic arguments, operators, conversion operators, indexers, constructors, static constructors,
    /// events, fields, constants, enum members, delegates, explicit implementations (multi-argument
    /// generic and BCL interfaces included), extension methods, records — is encoded differently from the
    /// compiler. Every ID the compiler wrote must be produced by reflection over the same assembly.
    /// </summary>
    [Fact]
    public void EveryCompilerEmittedId_IsProducedForItsMember()
    {
        var assembly = Compile("Shapes", Shapes, out var documented);
        var produced = ImmutableHashSet<string>.Empty;
        foreach (var type in assembly.GetTypes())
        {
            if (XmlDocs.DocumentationId(type) is { } typeId)
                produced = produced.Add(typeId);
            foreach (var member in type.GetMembers(All))
                if (member is not Type && XmlDocs.DocumentationId(member) is { } id)
                    produced = produced.Add(id);
        }

        documented.Should().NotBeEmpty("CONTROL — the compiler emitted documentation");
        documented.Except(produced).Should().BeEmpty(
            "every documented member's compiler ID must be what DocumentationId computes for it");
    }

    [Fact]
    public void AConstructedGenericMethod_InheritsLikeItsDefinition()
    {
        var assembly = Compile("Inheritance", Inheritance, out _);
        var convert = assembly.GetType("Inheritance.Converter")!.GetMethod("Convert")!;
        docs.Summary(convert).Should().Be("Converts a value.", "CONTROL — the definition");
        docs.Summary(convert.MakeGenericMethod(typeof(int))).Should().Be("Converts a value.");
    }

    [Fact]
    public void OpenAndClosedGenerics_InheritFromTheBase_ThroughDefinitionAndConstruction()
    {
        var assembly = Compile("Inheritance", Inheritance, out _);
        docs.Summary(assembly.GetType("Inheritance.Closed")!.GetMethod("Run")!).Should().Be("Base Run.");
        docs.Summary(assembly.GetType("Inheritance.Closed")!.GetProperty("Value")!).Should().Be("Base Value.");
        var open = assembly.GetType("Inheritance.Open`1")!;
        docs.Summary(open.GetMethod("Run")!).Should().Be("Base Run.");
        docs.Summary(open.MakeGenericType(typeof(string)).GetMethod("Run")!).Should().Be("Base Run.");
    }

    /// <summary>
    /// <b>Should fail if</b> explicit qualifiers are compared loosely: <c>IFoo</c> vs <c>IFoo&lt;int&gt;</c>
    /// and <c>Outer&lt;T&gt;.IA</c> vs <c>Outer&lt;T&gt;.IB</c> collapse and read each other's summary —
    /// on the open definition and on a constructed type alike.
    /// </summary>
    [Fact]
    public void ExplicitImplementations_InheritFromExactlyTheirInterface()
    {
        var assembly = Compile("Inheritance", Inheritance, out _);
        foreach (var type in new[] { assembly.GetType("Inheritance.Explicit`1")!, assembly.GetType("Inheritance.Explicit`1")!.MakeGenericType(typeof(int)) })
        {
            var names = type.GetProperties(All).ToDictionary(p => p.Name[..p.Name.LastIndexOf('.')], p => docs.Summary(p));
            names.Should().HaveCount(4);
            names.Single(kv => kv.Key == "Inheritance.IFoo").Value.Should().Be("IFoo name.");
            names.Single(kv => kv.Key.StartsWith("Inheritance.IFoo<", StringComparison.Ordinal)).Value.Should().Be("IFoo of T name.");
            names.Single(kv => kv.Key.EndsWith(".IA", StringComparison.Ordinal)).Value.Should().Be("IA name.");
            names.Single(kv => kv.Key.EndsWith(".IB", StringComparison.Ordinal)).Value.Should().Be("IB name.");
        }
        docs.Summary(assembly.GetType("Inheritance.Implicit")!.GetProperty("Name")!).Should().Be("IFoo name.");
    }

    /// <summary>
    /// <b>Should fail if</b> the record <c>&lt;param&gt;</c> fallback runs only for a direct read: a
    /// cref to a record property reads empty.
    /// </summary>
    [Fact]
    public void ACrefToARecordProperty_ReadsItsParam()
    {
        var assembly = Compile("Inheritance", Inheritance, out _);
        docs.Summary(assembly.GetType("Inheritance.Tagged")!.GetProperty("Label")!).Should().Be("The record's label.", "CONTROL — the direct read");
        docs.Summary(assembly.GetType("Inheritance.RecordAlias")!.GetProperty("Alias")!).Should().Be("The record's label.");
        // The compiler writes a positional record's property its own summary, so the case that needs the
        // fallback is a property documented ONLY as its type's <param> (MeshWeaver's ExecutionRequest shape).
        docs.Summary(assembly.GetType("Inheritance.Manual")!.GetProperty("Label")!).Should().Be("The manual label.", "CONTROL — the direct read");
        docs.Summary(assembly.GetType("Inheritance.RecordAlias")!.GetProperty("ManualAlias")!).Should().Be("The manual label.");
    }

    /// <summary>
    /// <b>Should fail if</b> a LOCATED assembly whose sidecar is missing probes the base or current
    /// directory: it would read another assembly's same-named documentation.
    /// </summary>
    [Fact]
    public void ALocatedAssemblyWithoutItsSidecar_ReadsUndocumented_EvenIfTheBaseDirectoryHasOne()
    {
        var name = "XmlDocsLocated" + Guid.NewGuid().ToString("N");
        var decoy = Path.Combine(AppContext.BaseDirectory, "DynamicNode_" + name + ".xml");
        File.WriteAllText(decoy, """
            <?xml version="1.0"?><doc><members>
            <member name="T:Probe.Subject"><summary>Another generation's docs.</summary></member>
            </members></doc>
            """);
        try
        {
            var directory = Path.Combine(root, "located");
            var assembly = XmlDocsFixtureAssembly.Emit(directory, name,
                "namespace Probe; /// <summary>Own docs.</summary>\npublic sealed class Subject { }", out _);
            var subject = assembly.GetType("Probe.Subject")!;
            File.Delete(Path.Combine(directory, "DynamicNode_" + name + ".xml"));

            docs.Summary(subject).Should().BeEmpty("a located assembly reads only its own sidecar");
        }
        finally
        {
            File.Delete(decoy);
        }
    }
}
