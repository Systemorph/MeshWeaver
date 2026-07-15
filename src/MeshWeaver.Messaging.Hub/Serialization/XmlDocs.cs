using System.Reflection;
using Namotion.Reflection;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Thread-safe access to Namotion.Reflection XML-docs lookups — the ONLY sanctioned way to
/// read doc summaries anywhere in the platform (never call <c>GetXmlDocsSummary</c> directly).
///
/// <para>Why: Namotion caches one <c>XDocument</c> per assembly in a static
/// <c>CachingXDocument</c>, and XLinq documents lazily materialize their node graph on FIRST
/// enumeration. Two hubs building concurrently on pool threads (<c>TypeRegistry.WithType</c> →
/// <c>TypeDefinition..ctor</c> → docs summary) enumerated the same cached document at the same
/// time and tore its object graph: <c>AccessViolationException</c> in
/// <c>XContainer.GetElements</c> → SIGSEGV (exit 139) — FutuRe.Test dying mid-fixture-init on
/// the CI runner (and reproducibly under emulated linux-amd64), invisible on fast local
/// hardware where the first-enumeration window is microseconds.</para>
///
/// <para>One process-wide lock serialises every read. Docs lookups happen on registration /
/// catalog-render paths only, and after Namotion's per-assembly cache is warm each lookup is a
/// dictionary hit — contention is irrelevant next to memory safety. This is a synchronous lock
/// around a synchronous third-party call (nothing is awaited under it), not an async gate.</para>
/// </summary>
public static class XmlDocs
{
    private static readonly System.Threading.Lock Gate = new();

    /// <summary>Thread-safe <c>GetXmlDocsSummary</c> for a type.</summary>
    public static string Summary(Type type)
    {
        lock (Gate)
            return type.GetXmlDocsSummary();
    }

    /// <summary>Thread-safe <c>GetXmlDocsSummary</c> for a member (property, method, field).</summary>
    public static string Summary(MemberInfo member)
    {
        lock (Gate)
            return member.GetXmlDocsSummary();
    }
}
