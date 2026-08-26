using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace MeshWeaver.Social;

/// <summary>
/// Merging LinkedIn's <c>userinfo</c> identity (display name + profile photo) into a social
/// profile's stored content — the pure half of "sync my profile from the account", split out so
/// the merge rules are unit-testable without an OAuth round trip.
///
/// <para>The rule is READ-MERGE-WRITE, never replace: LinkedIn owns the display name and the
/// photo, the mesh owns everything else (network, headline, owner, handle, profile URL). A
/// wholesale content write would silently drop those.</para>
///
/// <para>🚨 <b>The result is a <see cref="JsonObject"/>, and that is load-bearing (issue #52).</b>
/// This used to return a <c>Dictionary&lt;string, object?&gt;</c> carrying an explicit
/// <c>["$type"] = "SocialProfile"</c> entry — which the platform's polymorphic converter DROPS,
/// stamping the dictionary's own CLR collection name in its place. Every connect therefore rewrote
/// a live profile to <c>"$type":"Dictionary`2[String,Object]"</c>, after which the profile read as
/// empty and its posts could not be approved. <see cref="NodeContentJson"/> carries the full
/// reasoning; the short version is that only a JSON shape is written verbatim.</para>
/// </summary>
public static class LinkedInIdentitySync
{
    /// <summary>Content key of the profile's display name.</summary>
    public const string DisplayNameKey = "displayName";

    /// <summary>Content key of the profile's avatar.</summary>
    public const string ImageUrlKey = "imageUrl";

    /// <summary>
    /// The <c>$type</c> a <c>SocialMedia/Profile</c> node's content carries. Used only as the
    /// FALLBACK: an existing discriminator always wins, because the stored node knows its own type
    /// and this module — compiled, and unable to reference a dynamic NodeType's content class —
    /// does not.
    /// </summary>
    public const string ProfileContentType = "SocialProfile";

    /// <summary>
    /// The profile content with LinkedIn's <paramref name="displayName"/> and
    /// <paramref name="pictureUrl"/> applied over whatever is already stored. Reads the existing
    /// content in WHATEVER shape it arrives — a typed record, a <c>JsonElement</c>, or a
    /// dictionary — because content typing depends on which hub last touched it. A null or blank
    /// incoming value leaves the stored one alone: an account that exposes no photo must not
    /// erase a good one. The <c>$type</c> discriminator is preserved (or restored, on a profile
    /// an earlier build already damaged). Pure.
    /// </summary>
    /// <param name="existingContent">The profile's content as stored, in any shape.</param>
    /// <param name="displayName">LinkedIn's display name; ignored when blank.</param>
    /// <param name="pictureUrl">LinkedIn's profile photo URL; ignored when blank.</param>
    public static JsonObject Merge(object? existingContent, string? displayName, string? pictureUrl)
    {
        var updates = new List<KeyValuePair<string, object?>>(2);
        if (!string.IsNullOrWhiteSpace(displayName))
            updates.Add(new(DisplayNameKey, displayName!.Trim()));
        if (!string.IsNullOrWhiteSpace(pictureUrl))
            updates.Add(new(ImageUrlKey, pictureUrl!.Trim()));
        return NodeContentJson.Merge(existingContent, ProfileContentType, updates);
    }
}
