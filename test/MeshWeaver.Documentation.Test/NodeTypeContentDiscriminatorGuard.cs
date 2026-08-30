using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for MeshWeaver#2729: a node type that declares a CLR content type
/// (<c>AddMeshDataSource(s =&gt; s.WithContentType&lt;T&gt;())</c>) must ALSO register that type's
/// <c>$type</c> discriminator on the builder, so every hub in the mesh can read the node — not only
/// the per-node hub the data source configures.
///
/// <para><b>The defect this pins is silent, and that is the whole reason it needs a guard.</b> On
/// <c>memex.systemorph.com</c> the portal logged, at boot:</para>
/// <code>
/// MeshNodeStreamCache.GetStream: Content for Admin/_GraphSubscription/inbox stayed an untyped
/// JsonElement after deserialization (TypeRegistry lacks the $type discriminator)
/// </code>
/// <para>…because <c>AddGraphSubscriptionType</c> declared <c>WithContentType&lt;GraphSubscriptionState&gt;()</c>
/// and nothing else. Per AGENTS.md that warning means the reader gets a <b>silent null</b>: the
/// inbound-mail subscription renewal could not see its own state, so inbound mail on that install was
/// dead with no error, no exception and nothing to grep for. Seven node types were in that position
/// when this guard was written; the six besides the reported one had simply not been read from
/// another hub yet.</para>
///
/// <para><b>Why a guard rather than seven fixes.</b> The two halves are joined by nothing — the
/// declaration is inside a <c>HubConfiguration</c> lambda on a MeshNode, the registration is a
/// separate statement in the enclosing <c>Add…Type</c> method, and omitting the second compiles,
/// starts, serves and passes every test. This is the same class as the
/// <see cref="HandWovenGateRatchetGuard"/> subject: a contract whose halves can drift with nothing
/// asserting they agree.</para>
///
/// <para>The three spellings below are all in live use and all correct; the guard accepts any of
/// them rather than legislating one, because which is right depends on whether the type crosses the
/// mesh hub only or every hub.</para>
/// </summary>
public class NodeTypeContentDiscriminatorGuard
{
    /// <summary>Where node types are declared. Both trees ship in the portal image.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex"];

    /// <summary>
    /// Declarations that are not a concrete content type and cannot be registered: the framework's
    /// own generic definitions and re-exports of <c>WithContentType&lt;T&gt;</c>.
    /// </summary>
    private static readonly HashSet<string> NotAContentType = new(StringComparer.Ordinal) { "T", "TContent", "TValue" };

    private static readonly Regex Declaration = new(@"WithContentType\s*<\s*([A-Za-z0-9_.]+)\s*>", RegexOptions.Compiled);

    /// <summary>
    /// The floor that stops this guard passing vacuously. If the scan ever finds fewer declarations
    /// than this, the scan itself has broken (a moved tree, a renamed API) and the guard must fail
    /// LOUDLY rather than report a clean tree it never looked at.
    /// </summary>
    private const int MinimumDeclarationsExpected = 25;

    [Fact(Timeout = 60000)]
    public void EveryDeclaredContentType_AlsoRegistersItsDiscriminator()
    {
        var root = SourceScan.FindRepoRoot();
        var files = SourceScan.SourceFiles(root, ScannedRoots).ToArray();

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var registered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));

            foreach (Match m in Declaration.Matches(code))
            {
                var name = ShortName(m.Groups[1].Value);
                if (NotAContentType.Contains(name)) continue;
                declared.TryAdd(name, SourceScan.Relative(root, file));
            }

            foreach (var name in RegisteredNames(code))
                registered.Add(name);
        }

        declared.Count.Should().BeGreaterThanOrEqualTo(MinimumDeclarationsExpected,
            "the scan must actually find the WithContentType declarations it is guarding — a count "
            + "below the floor means the scan broke, not that the tree is clean");

        var missing = declared
            .Where(d => !registered.Contains(d.Key))
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "every content type a node type declares must also register its $type discriminator, or "
            + "a reader on another hub silently gets an untyped JsonElement (MeshWeaver#2729). "
            + "Missing: "
            + string.Join(", ", missing.Select(m => $"{m.Key} (declared in {m.Value})"))
            + ". Fix by adding, in that type's Add…Type method, one of: "
            + "builder.ConfigureHub(config => config.WithType<X>(nameof(X)));  |  "
            + "builder.WithMeshType<X>();  |  typeRegistry.WithType(typeof(X), nameof(X)) in "
            + "MeshNodeExtensions. Never by deleting the WithContentType declaration.");
    }

    /// <summary>
    /// 🚨 The guard's own failure mode, asserted rather than assumed. A ratchet that cannot fail is
    /// not a ratchet — and the way this one would fail silently is by treating an unregistered type
    /// as registered, which is exactly what a too-permissive <see cref="RegisteredNames"/> would do.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheGuardDetectsAnUnregisteredType()
    {
        RegisteredNames("builder.ConfigureHub(c => c.WithType<Widget>(nameof(Widget)));")
            .Should().Contain("Widget");
        RegisteredNames("builder.WithMeshType<Widget>();").Should().Contain("Widget");
        RegisteredNames("typeRegistry.WithType(typeof(Widget), nameof(Widget));").Should().Contain("Widget");

        // The declaration alone — the #2729 shape — must NOT count as a registration.
        RegisteredNames("HubConfiguration = config => config.AddMeshDataSource(s => s.WithContentType<Widget>()),")
            .Should().NotContain("Widget");
    }

    /// <summary>Every type name this code registers a discriminator for, by any of the three spellings.</summary>
    private static IEnumerable<string> RegisteredNames(string code)
    {
        foreach (Match m in Regex.Matches(code, @"With(?:Mesh)?Type\s*<\s*([A-Za-z0-9_.]+)\s*>"))
            yield return ShortName(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(code, @"With(?:Mesh)?Type\s*\(\s*typeof\s*\(\s*([A-Za-z0-9_.]+)\s*\)"))
            yield return ShortName(m.Groups[1].Value);
    }

    private static string ShortName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }
}
