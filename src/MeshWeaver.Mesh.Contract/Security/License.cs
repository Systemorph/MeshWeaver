using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A license, as a first-class mesh node — the terms a package is offered under, addressable by
/// path so a manifest points AT one rather than restating it.
///
/// <para>Why a node and not a string on the package: the same terms apply to many packages, the
/// full text has to be shown before someone accepts it, and an acceptance has to reference
/// something stable. A per-package copy of a license string gives you none of that — it drifts,
/// it cannot be displayed, and there is nothing for an <see cref="LicenseAcceptance"/> to point
/// to. Licenses therefore live in their own catalog (<c>License/{SpdxId}</c>) exactly like agents
/// and skills, and a package's <c>license</c> field carries the SPDX id.</para>
/// </summary>
public record LicenseContent
{
    /// <summary>SPDX identifier — the canonical, machine-comparable name (<c>Apache-2.0</c>,
    /// <c>MIT</c>). Dual licensing uses an SPDX expression: <c>MIT OR Apache-2.0</c>.</summary>
    public string SpdxId { get; init; } = "";

    /// <summary>Human-readable name shown in the UI ("Apache License 2.0").</summary>
    public string Name { get; init; } = "";

    /// <summary>Canonical URL of the license text.</summary>
    public string Url { get; init; } = "";

    /// <summary>The full license text. Held on the node so acceptance can show the ACTUAL terms
    /// rather than a link the reader is asked to trust.</summary>
    public string Body { get; init; } = "";

    /// <summary>One-line plain-language summary — what the reader may do. Never a substitute for
    /// <see cref="Body"/>; a summary is an aid, the text is the agreement.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Whether installing something under this license requires an explicit, recorded acceptance.
    /// FALSE for permissive licenses that grant use without conditions on the user (Apache-2.0,
    /// MIT): demanding a click to accept terms that ask nothing of them is friction pretending to
    /// be diligence. TRUE for commercial or restrictive terms, where consent is the point.
    /// </summary>
    public bool RequiresAcceptance { get; init; }

    /// <summary>Whether this license obliges a distributor to publish source. Recorded because it
    /// is the property that decides whether a package can ship without its source — the platform
    /// deliberately ships some packages source-partial.</summary>
    public bool RequiresSourceDisclosure { get; init; }
}

/// <summary>
/// A recorded acceptance of a <see cref="LicenseContent"/> by one user — written only for licenses
/// whose <see cref="LicenseContent.RequiresAcceptance"/> is set, and stored in the accepting user's
/// own partition, so it is evidence the user holds rather than a claim the platform makes about them.
/// </summary>
public record LicenseAcceptance
{
    /// <summary>SPDX id of the accepted license.</summary>
    public string SpdxId { get; init; } = "";

    /// <summary>The package the acceptance was given for (a license is accepted in a context).</summary>
    public string PackageId { get; init; } = "";

    /// <summary>ObjectId of the accepting user.</summary>
    public string UserId { get; init; } = "";

    /// <summary>When it was accepted.</summary>
    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>The license text's content hash at acceptance time. Terms can be revised; an
    /// acceptance is only meaningful against the text that was actually shown.</summary>
    public string BodyHash { get; init; } = "";
}

/// <summary>The licenses the platform ships as its own catalog, and the SPDX expression the
/// platform itself is offered under.</summary>
public static class WellKnownLicenses
{
    /// <summary>Partition holding the license catalog.</summary>
    public const string Partition = "License";

    /// <summary>Node path for an SPDX id.</summary>
    public static string PathFor(string spdxId) => $"{Partition}/{spdxId}";

    /// <summary>
    /// The platform's own terms: <b>MIT OR Apache-2.0</b> — the recipient chooses either.
    ///
    /// <para>Dual licensing is what lets both audiences be served without compromise: Apache-2.0
    /// carries an explicit patent grant (real protection for a platform that executes user code),
    /// while MIT stays compatible with GPLv2, which Apache-2.0 is not. Offering both means a
    /// downstream GPLv2 project can still use this, and an enterprise still gets the patent grant.
    /// Neither obliges anyone to publish source, which is the property that matters here: packages
    /// may ship source-partial, and nothing in either license turns that into a disclosure duty.</para>
    /// </summary>
    public const string PlatformSpdxExpression = "MIT OR Apache-2.0";

    /// <summary>Ids of the licenses shipped in the catalog.</summary>
    public static readonly ImmutableArray<string> Shipped = ["Apache-2.0", "MIT"];
}
