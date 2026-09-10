// 🚨 THE SAME SHORT NAME, THREE DECLARATIONS — the production shape the retype defect needs.
//
// A content type's `$type` discriminator is its BARE CLR name, unique only inside its own package:
// IMeshContentTypeRegistry's own remarks record one customer repo shipping `Currency` four times
// (Reinsurance/, ClaimsDeepfield/, UWDeepfield/, Ifrs17/) and eleven further names twice or more.
// ObjectAsExtensions.As recovers a foreign runtime type ONLY when that short name matches — which
// is what makes a cross-package retype convert silently instead of declining.
//
// These live in their own file because the rest of the suite uses a FILE-SCOPED namespace, and a
// file-scoped namespace cannot be mixed with the block-scoped ones the collision requires.

namespace MeshWeaver.Graph.Test.RetypeFrom
{
    /// <summary>
    /// One package's <c>PackageContent</c> — what the node holds BEFORE the retype. Declared by
    /// the <c>RetypeBeforeProbe</c> NodeType, so it is registered and reads back typed.
    /// </summary>
    /// <param name="Title">A member the other package's record also declares.</param>
    /// <param name="Sequence">A member it does NOT declare — silently dropped by a wrong-authority
    /// conversion, which is the loss the defect causes.</param>
    public record PackageContent(string Title, int Sequence);
}

namespace MeshWeaver.Graph.Test.RetypeTo
{
    /// <summary>
    /// ANOTHER package's <c>PackageContent</c> — what the retyping update proposes. Same short
    /// name, different members. <c>Reason</c> is nullable, so the other package's bytes
    /// deserialise into this record WITHOUT error: that is what makes the wrong-authority
    /// conversion silent rather than loud.
    /// </summary>
    /// <param name="Title">Shared with the other package's record.</param>
    /// <param name="Reason">Absent over there, so it materialises as null.</param>
    public record PackageContent(string Title, string? Reason);
}

namespace MeshWeaver.Graph.Test.RetypeForeign
{
    /// <summary>
    /// A THIRD declaration of the same short name, standing in for "the same record compiled into
    /// another collectible assembly" — case 3 of <c>ObjectAsExtensions</c>'s trap-door list, which
    /// every NodeType recompile mints for real. Structurally identical to
    /// <see cref="RetypeFrom.PackageContent"/> so a same-short-name recovery round-trips it
    /// losslessly; it is a DIFFERENT CLR type, so <c>is</c> / <c>as</c> against the local record
    /// still answers null.
    /// </summary>
    /// <param name="Title">As in the other packages.</param>
    /// <param name="Sequence">As in <see cref="RetypeFrom.PackageContent"/>.</param>
    public record PackageContent(string Title, int Sequence);
}
