using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The naming SCHEME a framework build identity is written in. Two identities in different schemes
/// are two different facts about a build, not two values of one fact — so they can never be equal
/// and their inequality says nothing.
/// </summary>
public enum ModuleIdentityScheme
{
    /// <summary>Nothing was stated (null, blank).</summary>
    Unstated,

    /// <summary>The API-SURFACE identity, <c>s&lt;32 hex&gt;</c> — what a process with a surface
    /// manifest resolves for itself (<c>FrameworkBuildIdentity.FrameworkVersion</c>).</summary>
    Surface,

    /// <summary>The COMMIT identity, <c>g&lt;sha&gt;</c> — what CI stamps into an assembly and what
    /// every module-pack lane therefore states for its bundles.</summary>
    Commit,

    /// <summary>A bare 32-hex MVID — the anchor's content identity, stated by a producer packing
    /// against a platform whose anchor carries no stamp (a local build).</summary>
    Content,

    /// <summary>Anything else — compared by exact equality against other values of this shape, and
    /// never against a value of a named scheme above.</summary>
    Other,
}

/// <summary>What comparing a stated identity against what a platform states came to.</summary>
public enum ModuleIdentityVerdict
{
    /// <summary>One side stated nothing, so there is nothing to compare — absence of evidence
    /// (rule R2 of <c>Doc/Architecture/ModuleAdoptionPolicy</c>), never evidence of difference.</summary>
    NotStated,

    /// <summary>The stated identity IS one of the platform's readings: these bytes were built
    /// against this platform build, measured rather than assumed.</summary>
    Matches,

    /// <summary>The platform states a reading in the SAME scheme and it is a different value — the
    /// bytes were built against another platform build.</summary>
    Differs,

    /// <summary>The platform states no reading in the stated identity's scheme, so nothing here can
    /// say whether the bytes belong to this build. 🚨 NOT the same as <see cref="Differs"/>, and the
    /// difference is the whole of MeshWeaver#4161's blind spot: a bundle stating <c>g&lt;sha&gt;</c>
    /// against a portal resolving <c>s&lt;hash&gt;</c> compared unequal forever, which read as
    /// "built for another platform" when it only ever meant "these two sentences are not about the
    /// same thing".</summary>
    NotComparable,
}

/// <summary>
/// 🚨 <b>THE one rule for comparing a module bundle's recorded framework identity against what the
/// platform running it states</b> — scheme-aware, so "different value" and "different kind of
/// value" are never confused.
///
/// <para><b>The defect this exists to close (measured, memex.systemorph.com, 2026-09-16).</b> Every
/// module-pack lane states a bundle's <c>frameworkMvid</c> by READING the platform's own
/// <c>MeshWeaver.Compiler.dll</c> (or, for a source-built platform, its commit) — a
/// <c>g&lt;sha&gt;</c> token. Every portal resolves its own identity from the surface manifest — an
/// <c>s&lt;hash&gt;</c> token. <c>ModuleActivationBoot.ComputeEffectiveModuleEntries</c> compared
/// the two with ordinal equality (#4161) and declined the store copy of every module the image also
/// ships, on every boot, for ever — the registry lane was structurally dead for those eight modules
/// and no publication could revive it. Meanwhile the activation report called them PENDING and
/// <c>/health</c> promised that "a restart activates them": two operators restarted the deployment
/// for nothing, and the entry still listed all eight afterwards.</para>
///
/// <para><b>What changes and what deliberately does not.</b> A platform may state SEVERAL readings
/// of itself — the surface identity it compiles content against and the producer reading a packer
/// would take off its anchor (<c>FrameworkBuildIdentity.ProducerStatedIdentity</c>) — and a stated
/// identity that equals ANY of them is a match, which is how a bundle packed by the very build that
/// produced this image is now adopted instead of declined. Where the schemes do not meet, the
/// answer is <see cref="ModuleIdentityVerdict.NotComparable"/> and the CALLER decides what to do
/// with it: the boot discriminator still prefers the image's own copy (which is correct for this
/// platform by construction, and un-declining it would reinstate Plugins#1483), but it now says so
/// in words that are true, and every status surface reports the state with the remedy that actually
/// clears it.</para>
///
/// <para>Pure and total: schemes in, verdict out. No file system, no process state — the readings
/// are supplied by the caller, so a test can stand up a second platform without one.</para>
/// </summary>
public static class ModuleFrameworkIdentity
{
    /// <summary>
    /// Which scheme <paramref name="identity"/> is written in. Shape-based and total — an
    /// unrecognised token is <see cref="ModuleIdentityScheme.Other"/>, which compares only against
    /// other unrecognised tokens.
    /// </summary>
    /// <param name="identity">The identity token, or null/blank.</param>
    public static ModuleIdentityScheme SchemeOf(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return ModuleIdentityScheme.Unstated;
        var value = identity.Trim();
        if (value.Length == 33 && value[0] is 's' or 'S' && IsHex(value.AsSpan(1)))
            return ModuleIdentityScheme.Surface;
        // A commit identity is 'g' + a git sha — the full 40 in every lane that writes one, but a
        // short sha is still a commit and must not be mistaken for something else.
        if (value.Length is >= 8 and <= 41 && value[0] is 'g' or 'G' && IsHex(value.AsSpan(1)))
            return ModuleIdentityScheme.Commit;
        if (value.Length == 32 && IsHex(value.AsSpan()))
            return ModuleIdentityScheme.Content;
        return ModuleIdentityScheme.Other;
    }

    /// <summary>
    /// Compares what a bundle STATED against what the platform states about itself.
    /// </summary>
    /// <param name="stated">The bundle's recorded framework identity
    /// (<see cref="ModuleActivationEntry.FrameworkMvid"/>).</param>
    /// <param name="platformReadings">Every identity this platform states about itself — in
    /// production the surface identity and the producer reading; a caller that knows only one
    /// passes one. Null or empty states nothing, exactly as a blank
    /// <paramref name="stated"/> does.</param>
    public static ModuleIdentityMatch Compare(
        string? stated, IReadOnlyCollection<string>? platformReadings)
    {
        var scheme = SchemeOf(stated);
        var readings = Readings(platformReadings);
        if (scheme == ModuleIdentityScheme.Unstated || readings.IsEmpty)
            return new ModuleIdentityMatch(ModuleIdentityVerdict.NotStated, null, scheme, readings);

        var value = stated!.Trim();
        foreach (var reading in readings)
            if (string.Equals(reading, value, StringComparison.Ordinal))
                return new ModuleIdentityMatch(ModuleIdentityVerdict.Matches, reading, scheme, readings);

        foreach (var reading in readings)
            if (SchemeOf(reading) == scheme)
                return new ModuleIdentityMatch(ModuleIdentityVerdict.Differs, reading, scheme, readings);

        return new ModuleIdentityMatch(ModuleIdentityVerdict.NotComparable, null, scheme, readings);
    }

    /// <summary>The distinct, non-blank readings, in the order given — the normalisation every
    /// caller would otherwise repeat.</summary>
    public static ImmutableArray<string> Readings(IReadOnlyCollection<string>? readings) =>
        readings is null
            ? []
            : [.. readings
                .Where(reading => !string.IsNullOrWhiteSpace(reading))
                .Select(reading => reading.Trim())
                .Distinct(StringComparer.Ordinal)];

    /// <summary>The words for <see cref="ModuleIdentityScheme"/>, for a message a person reads.</summary>
    public static string Name(ModuleIdentityScheme scheme) => scheme switch
    {
        ModuleIdentityScheme.Surface => "an API-surface identity (s<hash>)",
        ModuleIdentityScheme.Commit => "a commit identity (g<sha>)",
        ModuleIdentityScheme.Content => "a content identity (the anchor's MVID)",
        ModuleIdentityScheme.Unstated => "nothing",
        _ => "an identity of an unrecognised shape",
    };

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            if (!char.IsAsciiHexDigit(c))
                return false;
        return value.Length > 0;
    }
}

/// <summary>
/// One comparison's answer: the verdict, the platform reading it was reached against (null when
/// none was comparable), and the inputs' shapes — enough for a caller to write a sentence naming
/// both sides without re-deriving anything.
/// </summary>
/// <param name="Verdict">What the comparison came to.</param>
/// <param name="ComparedWith">The platform reading the stated identity was measured against —
/// the matching one for <see cref="ModuleIdentityVerdict.Matches"/>, the same-scheme one for
/// <see cref="ModuleIdentityVerdict.Differs"/>, null otherwise.</param>
/// <param name="StatedScheme">The scheme the bundle stated in.</param>
/// <param name="PlatformReadings">The platform's readings, normalised.</param>
public readonly record struct ModuleIdentityMatch(
    ModuleIdentityVerdict Verdict,
    string? ComparedWith,
    ModuleIdentityScheme StatedScheme,
    ImmutableArray<string> PlatformReadings)
{
    /// <summary>True when the bundle was NOT shown to belong to this platform build — the verdict
    /// the image-copy preference acts on (<see cref="ModuleIdentityVerdict.Differs"/> or
    /// <see cref="ModuleIdentityVerdict.NotComparable"/>).</summary>
    public bool IsNotThisPlatform =>
        Verdict is ModuleIdentityVerdict.Differs or ModuleIdentityVerdict.NotComparable;

    /// <summary>
    /// The sentence naming what was compared and how it came out — shared by the boot skip line,
    /// the activation report and every package card, so an operator is never told two stories.
    /// </summary>
    /// <param name="stated">The identity the bundle stated.</param>
    public string Describe(string? stated) => Verdict switch
    {
        ModuleIdentityVerdict.Matches =>
            $"built against this platform build ({ComparedWith})",
        ModuleIdentityVerdict.Differs =>
            $"built for another platform (framework {stated}; this deployment runs {ComparedWith})",
        ModuleIdentityVerdict.NotComparable =>
            $"built against a platform this one cannot compare itself to: the bundle states "
            + $"{ModuleFrameworkIdentity.Name(StatedScheme)} ({stated}) and this deployment states "
            // A DEFAULT struct carries an uninitialised array; printing "(none)" for it is the same
            // discipline the rest of this file keeps — a reading that cannot be shown is never
            // allowed to throw inside a diagnostic.
            + $"{(PlatformReadings.IsDefaultOrEmpty ? "(none)" : string.Join(", ", PlatformReadings))}"
            + " — no reading in the same scheme, so nothing here can show these bytes belong to "
            + "this build",
        _ => "nothing was stated on one side, so the identity decides nothing",
    };
}
