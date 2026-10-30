using System.Reflection;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// The documentation-ID encoder and the retired static summary forms. The mesh's reader is
/// <see cref="XmlDocsReader"/>, reached through <see cref="XmlDocsExtensions"/>. Read
/// summaries with <c>hub.XmlDocs().Summary(...)</c>; see <c>Doc/Architecture/XmlDocumentationReader</c>.
/// </summary>
public static class XmlDocs
{
    /// <summary>
    /// The documentation comment ID the C# compiler writes for <paramref name="member"/>
    /// (<c>T:</c>, <c>P:</c>, <c>M:</c>, <c>F:</c>, <c>E:</c>), or <c>null</c> when it has none
    /// (a generic parameter, a global method).
    /// </summary>
    /// <param name="member">The type or member.</param>
    /// <returns>The ID, e.g. <c>M:Ns.C.M``1(``0,System.Int32@)</c>.</returns>
    public static string? DocumentationId(MemberInfo member) => DocCommentId.For(member);

    /// <summary>
    /// Retired static form. It owns no cache — a static one would outlive the mesh — so every call
    /// locates and parses the assembly's documentation file again. Kept so compiled callers (in-mesh
    /// sources included) keep working; use <c>hub.XmlDocs().Summary(type)</c>.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The rendered summary text.</returns>
    [Obsolete("Use hub.XmlDocs().Summary(type): the mesh-owned reader parses each documentation file once.")]
    public static string Summary(Type type) => new XmlDocsReader().Summary(type);

    /// <summary>
    /// Retired static form; see <see cref="Summary(Type)"/>. Use <c>hub.XmlDocs().Summary(member)</c>.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <returns>The rendered summary text.</returns>
    [Obsolete("Use hub.XmlDocs().Summary(member): the mesh-owned reader parses each documentation file once.")]
    public static string Summary(MemberInfo member) => new XmlDocsReader().Summary(member);
}

/// <summary>Reaches the mesh-owned <see cref="XmlDocsReader"/> from a hub or its type registry.</summary>
public static class XmlDocsExtensions
{
    /// <summary>The mesh's XML-documentation reader, shared by every hub of the mesh.</summary>
    /// <param name="hub">Any hub of the mesh.</param>
    /// <returns>The reader.</returns>
    public static XmlDocsReader XmlDocs(this IMessageHub hub) => hub.TypeRegistry.XmlDocs();

    /// <summary>
    /// Emits once the documentation of <paramref name="members"/> is in memory: at once when it already
    /// is, otherwise after the cold reads have run on the mesh's I/O pool
    /// (<see cref="XmlDocsReader.WhenLoaded"/>). A view or handler that reads summaries composes its
    /// synchronous reads AFTER this emission, so the thread building it never reads a file.
    /// </summary>
    /// <param name="hub">Any hub of the mesh.</param>
    /// <param name="members">The types and members whose summaries are about to be read.</param>
    /// <returns>A single <see cref="System.Reactive.Unit"/>; cold.</returns>
    public static IObservable<System.Reactive.Unit> WhenXmlDocsLoaded(this IMessageHub hub, IEnumerable<MemberInfo> members)
        => hub.XmlDocs().WhenLoaded(members, hub.BlockingReadScheduler());

    /// <summary>
    /// The XML-doc summary of <paramref name="member"/> as a data-bound value
    /// (<see cref="XmlDocsReader.ObserveSummary"/>): emitted at once when in memory, otherwise after
    /// the cold read has run on the mesh's I/O pool.
    /// </summary>
    /// <param name="hub">Any hub of the mesh.</param>
    /// <param name="member">The type or member.</param>
    /// <returns>The rendered summary text, empty when there is none; cold, one value.</returns>
    public static IObservable<string> ObserveXmlDocsSummary(this IMessageHub hub, MemberInfo member)
        => hub.XmlDocs().ObserveSummary(member, hub.BlockingReadScheduler());

    /// <summary>
    /// <see cref="WhenXmlDocsLoaded(IMessageHub, IEnumerable{MemberInfo})"/> for the reader
    /// <paramref name="typeDefinition"/>'s registry owns — the one its
    /// <see cref="XmlDocsSummary(ITypeDefinition, MemberInfo)"/> reads — with <paramref name="hub"/>'s
    /// I/O pool doing the cold reads. A definition that is not a hub registry's uses the hub's reader.
    /// </summary>
    /// <param name="typeDefinition">The type definition whose summaries are about to be read.</param>
    /// <param name="hub">The hub whose mesh I/O pool performs any cold read.</param>
    /// <param name="members">The types and members whose summaries are about to be read.</param>
    /// <returns>A single <see cref="System.Reactive.Unit"/>; cold.</returns>
    public static IObservable<System.Reactive.Unit> WhenXmlDocsLoaded(this ITypeDefinition typeDefinition, IMessageHub hub, IEnumerable<MemberInfo> members)
        => (typeDefinition is TypeDefinition definition ? definition.DisplayMetadata.Docs : hub.XmlDocs())
            .WhenLoaded(members, hub.BlockingReadScheduler());

    // Optional on purpose: a bare messaging-only hub registers no I/O pools, and the reader then hops
    // the read onto the task pool itself rather than reading on the caller's thread.
    private static IBlockingReadScheduler? BlockingReadScheduler(this IMessageHub hub)
        => hub.ServiceProvider.GetService(typeof(IBlockingReadScheduler)) as IBlockingReadScheduler;

    /// <summary>The mesh's XML-documentation reader, reached through a hub's type registry.</summary>
    /// <param name="typeRegistry">A hub's type registry (<see cref="IMessageHub.TypeRegistry"/>).</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidOperationException">The registry is not a hub's type registry.</exception>
    public static XmlDocsReader XmlDocs(this ITypeRegistry typeRegistry)
        => typeRegistry is TypeRegistry registry
            ? registry.Docs
            : throw new InvalidOperationException(
                $"{typeRegistry.GetType().Name} is not a hub type registry, so it carries no mesh XML-docs reader.");

    /// <summary>
    /// The XML-doc summary of <paramref name="typeDefinition"/>'s type, read through the reader its
    /// registry owns; empty for a definition that is not a hub registry's.
    /// </summary>
    /// <param name="typeDefinition">The type definition.</param>
    /// <returns>The rendered summary text.</returns>
    public static string XmlDocsSummary(this ITypeDefinition typeDefinition)
        => typeDefinition is TypeDefinition definition ? definition.DisplayMetadata.Docs.Summary(definition.Type) : string.Empty;

    /// <summary>
    /// The XML-doc summary of <paramref name="member"/> (a member of, or a type related to,
    /// <paramref name="typeDefinition"/>), read through the reader its registry owns; empty for a
    /// definition that is not a hub registry's.
    /// </summary>
    /// <param name="typeDefinition">The type definition whose registry's reader is used.</param>
    /// <param name="member">The type or member to read.</param>
    /// <returns>The rendered summary text.</returns>
    public static string XmlDocsSummary(this ITypeDefinition typeDefinition, MemberInfo member)
        => typeDefinition is TypeDefinition definition
            ? member is Type type ? definition.DisplayMetadata.Docs.Summary(type) : definition.DisplayMetadata.Docs.Summary(member)
            : string.Empty;
}
