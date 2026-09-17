using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>What bundles for ONE framework identity actually carry, per NodeType</b> — the reading
/// <see cref="SealedPublicationIndex"/> deliberately does not perform (MeshWeaver#3845 hole 4).
///
/// <para><see cref="SealedSource"/> answers "the completion sentinel is present and every bundle it
/// lists is on disk", which is a statement about a DIRECTORY. What an adoption is decided on is a
/// per-type pair — one bundle entry's source fingerprint against that NodeType's own
/// <c>CurrentSourceFingerprint</c> — so nothing the seal says can name a NodeType. A publication can
/// be sealed, at the right commit, under the right identity, and still not contain the bundle a given
/// type needs: measured on MeshWeaver#3461, the two producers of
/// <c>prebuilt-bundles/&lt;identity&gt;/plugins</c> compose DIFFERENT module sets for one identity
/// (4 modules / 45 files against 5 / 46).</para>
///
/// <para>This is that missing half, as data: node path → the fingerprints bundles record for it, read
/// from the SAME bundle set <c>ShippedPrebuiltBundles.SeedForTypes</c> adopts from — the
/// image's <c>prebuilt/</c> directory plus this identity's complete published bundles, resolved
/// through each source's <c>_current</c> pointer. Reading a different set would let the gate hold a
/// type whose bytes the very next release pass would have landed.</para>
/// </summary>
/// <param name="ByNodePath">Node path → every source fingerprint a bundle records for it. A bundle
/// entry with no recorded fingerprint contributes <see cref="Unrecorded"/>, so "a legacy bundle names
/// this type" stays distinguishable from "no bundle names it".</param>
/// <param name="Bundles">How many bundle archives were read — the denominator of any claim made from
/// this reading.</param>
/// <param name="Outcome">Whether this reading is a STATEMENT about the world or a failure to look;
/// see <see cref="SealedReadOutcome"/> for why conflating those silently disables a gate.</param>
public sealed record PrebuiltBundleInventory(
    ImmutableDictionary<string, ImmutableHashSet<string>> ByNodePath,
    int Bundles,
    SealedReadOutcome Outcome)
{
    /// <summary>The placeholder a bundle entry with NO recorded source fingerprint contributes — a
    /// legacy bundle. It can never equal a computed fingerprint, so it never satisfies a hold; it
    /// only records that a bundle named the type.</summary>
    public const string Unrecorded = "(unrecorded)";

    /// <summary>Nothing read, and nothing to read — no image directory and no published root.</summary>
    public static PrebuiltBundleInventory NotConfigured { get; } =
        new(ImmutableDictionary<string, ImmutableHashSet<string>>.Empty, 0, SealedReadOutcome.NotConfigured);

    /// <summary>True when the reading is usable at all (it may still be empty, which is an answer).</summary>
    public bool IsUsable => Outcome is SealedReadOutcome.Read;

    /// <summary>
    /// Whether a bundle for this identity records <paramref name="fingerprint"/> for
    /// <paramref name="nodePath"/> — the one question hole 4's gate asks.
    /// </summary>
    /// <param name="nodePath">The NodeType's mesh path.</param>
    /// <param name="fingerprint">The source fingerprint the incoming tree would produce.</param>
    /// <returns>True when those bytes are on this instance's shelf.</returns>
    public bool Carries(string? nodePath, string? fingerprint)
        => nodePath is { Length: > 0 } path
           && fingerprint is { Length: > 0 } wanted
           && ByNodePath.TryGetValue(path, out var recorded)
           && recorded.Contains(wanted);

    /// <summary>Whether ANY bundle names this type, whatever fingerprint it records — the
    /// diagnostic half, so a hold can say "no bundle names it" apart from "one does, at another
    /// fingerprint".</summary>
    /// <param name="nodePath">The NodeType's mesh path.</param>
    /// <returns>True when a bundle entry names it.</returns>
    public bool Names(string? nodePath)
        => nodePath is { Length: > 0 } path && ByNodePath.ContainsKey(path);

    /// <summary>The fingerprints recorded for a type, for a hold's own sentence. Empty when none.</summary>
    /// <param name="nodePath">The NodeType's mesh path.</param>
    /// <returns>Every recorded fingerprint, or empty.</returns>
    public ImmutableHashSet<string> FingerprintsOf(string? nodePath)
        => nodePath is { Length: > 0 } path && ByNodePath.TryGetValue(path, out var recorded)
            ? recorded
            : [];

    /// <summary>
    /// Reads the inventory from disk. Pure over the file system and never throws: an unreadable
    /// enumeration is reported as <see cref="SealedReadOutcome.Unreadable"/> rather than as an empty
    /// one, because "no bundle carries this fingerprint" and "I could not look" call for opposite
    /// answers from the gate that consumes it (#3461's lesson, one reading over).
    ///
    /// <para>🚨 Every path is composed under the publication <c>_current</c> names
    /// (<see cref="ShippedPrebuiltBundles.PublicationDirectoryOf"/>), never under the source
    /// directory itself — reading the flat prefix while believing it read the generation is a silent
    /// wrong answer while both exist and a silent EMPTY one once #3461 phase 5 drops the flat copy.
    /// The bundle list also comes from the seal's own lines, so a torn publication contributes
    /// nothing rather than half of itself.</para>
    /// </summary>
    /// <param name="imageDirectory">The image's shipped <c>prebuilt/</c> directory, or null.</param>
    /// <param name="publishedRoot">The published bundle root, or null when this deployment consumes
    /// no CI bakes.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The inventory; never null.</returns>
    public static PrebuiltBundleInventory Read(
        string? imageDirectory, string? publishedRoot, string? identity, ILogger? logger = null)
    {
        var hasImage = !string.IsNullOrWhiteSpace(imageDirectory) && Directory.Exists(imageDirectory);
        var hasPublished = !string.IsNullOrWhiteSpace(publishedRoot) && !string.IsNullOrWhiteSpace(identity);
        if (!hasImage && !hasPublished)
            return NotConfigured;

        var byPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        var bundles = 0;
        var unreadable = false;

        void Fold(IEnumerable<string> archives)
        {
            foreach (var archive in archives)
            {
                BundleReader.Manifest? manifest;
                try
                {
                    manifest = BundleReader.ReadManifest(archive);
                }
                catch (Exception ex)
                {
                    // One unreadable archive is not a clean reading of the others: a hold taken from
                    // a partial inventory would be taken from a measurement that was not made.
                    logger?.LogWarning(ex,
                        "PrebuiltBundleInventory: {Bundle} could not be read — this reading is "
                        + "UNREADABLE, not short", archive);
                    unreadable = true;
                    continue;
                }
                bundles++;
                foreach (var entry in manifest?.Assemblies ?? [])
                {
                    if (entry.NodePath is not { Length: > 0 } path)
                        continue;
                    var fingerprint = entry.SourceFingerprint is { Length: > 0 } recorded
                        ? recorded
                        : Unrecorded;
                    byPath[path] = byPath.TryGetValue(path, out var known)
                        ? known.Add(fingerprint)
                        : [fingerprint];
                }
            }
        }

        if (hasImage)
        {
            try
            {
                Fold(Directory
                    .EnumerateFiles(imageDirectory!, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.Ordinal));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "PrebuiltBundleInventory: the image's prebuilt directory {Directory} could not be "
                    + "enumerated", imageDirectory);
                unreadable = true;
            }
        }

        if (hasPublished)
        {
            var identityDirectory = Path.Combine(publishedRoot!, identity!);
            try
            {
                if (Directory.Exists(identityDirectory))
                    // The seal's own bundle list, per source, under the generation `_current` names —
                    // the SAME enumeration the seeding pass adopts from.
                    Fold(ShippedPrebuiltBundles.CompletePublishedBundlesOf(identityDirectory, logger));
                else if (File.Exists(identityDirectory))
                    // Something is at exactly that path and it is not a directory — a half-finished
                    // layout migration looks like this from here, and it is not an empty shelf.
                    unreadable = true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "PrebuiltBundleInventory: the publications under {Directory} could not be read — "
                    + "this reading is UNREADABLE, not empty", identityDirectory);
                unreadable = true;
            }
        }

        return new PrebuiltBundleInventory(
            byPath.ToImmutable(), bundles,
            unreadable ? SealedReadOutcome.Unreadable : SealedReadOutcome.Read);
    }
}
