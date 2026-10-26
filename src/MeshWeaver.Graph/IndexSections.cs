using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Groups one level of the default index into SECTIONS by node type, so a space that mixes kinds
/// of material reads as an overview — "Opportunities", "Mails", "Contacts", "Documents" — instead
/// of one flat list in which a deal, a contact and a mail sit side by side.
///
/// <para><b>Only where it helps.</b> A level that holds one kind of node stays flat: a docs tree is
/// Markdown all the way down and gains no heading. Sections appear at whatever level mixes kinds,
/// so a client's root shows its deals and contacts and a deal shows its mails and documents.</para>
///
/// <para><b>The level's sort still decides.</b> A section takes the place of its FIRST member in
/// the level's order, and its members keep that order — the sort dropdown re-orders sections and
/// members alike, and nothing here sorts on its own.</para>
///
/// <para><b>Labels.</b> A section is named by the catalog key <c>nav.section.{nodeType}</c> when
/// the catalog has one, otherwise by the type's last path segment, split into words and made
/// plural (<c>Crm/Opportunity</c> → "Opportunities"), so a module's types are labelled without
/// core knowing them.</para>
/// </summary>
public static class IndexSections
{
    /// <summary>The catalog key prefix of a section label.</summary>
    public const string KeyPrefix = "nav.section.";

    /// <summary>The catalog key of the section for nodes without a type.</summary>
    public const string OtherKey = KeyPrefix + "untyped";

    /// <summary>
    /// The level's entries, grouped into one section per node type when there is more than one —
    /// else the entries unchanged. Pure.
    /// </summary>
    /// <param name="parent">The level's namespace — sections are identified below it.</param>
    /// <param name="rows">The level's entries in render order, each with its node type.</param>
    /// <param name="label">The section label for a node type (null for an untyped node).</param>
    public static ImmutableList<NodeNavigationEntry> Group(
        string parent,
        IReadOnlyList<(string? NodeType, NodeNavigationEntry Entry)> rows,
        Func<string?, string> label)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(label);
        if (rows.Select(r => r.NodeType ?? string.Empty).Distinct(StringComparer.Ordinal).Count() < 2)
            return [.. rows.Select(r => r.Entry)];
        // GroupBy keeps the order in which each key first appears — the section's place is its
        // first member's place in the level's sort.
        return [.. rows
            .GroupBy(r => r.NodeType ?? string.Empty, StringComparer.Ordinal)
            .Select(g =>
            {
                var nodeType = g.Key.Length == 0 ? null : g.Key;
                return new NodeNavigationEntry(label(nodeType), SectionPath(parent, nodeType))
                {
                    IsSection = true,
                    Children = [.. g.Select(r => r.Entry)],
                };
            })];
    }

    /// <summary>The identifier of a section — never a node path, so it links nowhere.</summary>
    /// <param name="parent">The level's namespace.</param>
    /// <param name="nodeType">The section's node type, null for untyped nodes.</param>
    public static string SectionPath(string parent, string? nodeType)
        => $"{parent}#{nodeType ?? "other"}";

    /// <summary>
    /// The viewer's label for a node type's section: the catalog's <c>nav.section.{nodeType}</c>
    /// when it exists, else <see cref="Plural"/> of the type's name.
    /// </summary>
    /// <param name="nodeType">The node type, null for untyped nodes.</param>
    /// <param name="locale">The viewer's locale.</param>
    public static string Label(string? nodeType, string? locale)
    {
        if (string.IsNullOrEmpty(nodeType))
            return LocalizationCatalog.Get(OtherKey, locale);
        var key = KeyPrefix + nodeType;
        if (LocalizationCatalog.ContainsKey(key))
            return LocalizationCatalog.Get(key, locale);
        var slash = nodeType.LastIndexOf('/');
        return Plural(Words(slash < 0 ? nodeType : nodeType[(slash + 1)..]));
    }

    /// <summary>"CourseInvite" → "Course Invite". Pure.</summary>
    /// <param name="name">A PascalCase name.</param>
    public static string Words(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }

    /// <summary>The English plural of a noun phrase's last word: Opportunity → Opportunities. Pure.</summary>
    /// <param name="noun">The noun phrase.</param>
    public static string Plural(string noun)
    {
        if (noun.Length == 0)
            return noun;
        var lower = noun.ToLowerInvariant();
        if (lower.EndsWith('y') && noun.Length > 1 && "aeiou".IndexOf(lower[^2]) < 0)
            return noun[..^1] + "ies";
        if (lower.EndsWith('s') || lower.EndsWith('x') || lower.EndsWith("ch", StringComparison.Ordinal) || lower.EndsWith("sh", StringComparison.Ordinal))
            return noun + "es";
        return noun + "s";
    }
}
