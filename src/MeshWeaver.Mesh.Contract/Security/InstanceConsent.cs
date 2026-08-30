namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The consent that gates an installation's OPEN registration at a registry: a platform admin of
/// this instance has read the privacy statement and the platform terms and accepted both, on a
/// date, under a name. Until this record exists the instance does not register itself, does not
/// obtain a token, and does not pull a catalogue — it starts, and asks.
///
/// <para>One record per instance, at <c>Admin/InstanceConsent</c>: consent is a property of the
/// DEPLOYMENT, given once by someone entitled to speak for it (a global admin), not of a user. The
/// texts are recorded by HASH so an audit can tell WHICH statement and WHICH terms were accepted
/// after either has been edited; a later edit does not revoke the consent — the operator who
/// changes the terms decides whether to ask again, by deleting this record.</para>
///
/// <para>Only OPEN registration (no bootstrap key) is gated. An instance provisioned with a
/// registration key was set up by an operator who accepted the terms on the fleet's side; asking
/// the same question of an unattended pod would leave it un-registered forever.</para>
/// </summary>
public record InstanceConsent
{
    /// <summary>The instance id the consent was given for — the one the registration will claim.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>The registry the instance registers at.</summary>
    public string RegistryUrl { get; init; } = "";

    /// <summary>SHA-256 hex of the privacy statement as shown when it was accepted.</summary>
    public string PrivacyStatementHash { get; init; } = "";

    /// <summary>SHA-256 hex of the platform terms as shown when they were accepted.</summary>
    public string TermsHash { get; init; } = "";

    /// <summary>When both were accepted.</summary>
    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>ObjectId of the global admin who accepted.</summary>
    public string AcceptedByUserId { get; init; } = "";

    /// <summary>Display name of the accepting admin.</summary>
    public string AcceptedByName { get; init; } = "";

    /// <summary>Email of the accepting admin.</summary>
    public string AcceptedByEmail { get; init; } = "";

    /// <summary>Whether this record carries a complete consent — both texts accepted by a named
    /// principal. An incomplete record gates exactly like an absent one.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(InstanceId)
        && !string.IsNullOrWhiteSpace(PrivacyStatementHash)
        && !string.IsNullOrWhiteSpace(TermsHash)
        && !string.IsNullOrWhiteSpace(AcceptedByUserId)
        && AcceptedAt != default;
}
