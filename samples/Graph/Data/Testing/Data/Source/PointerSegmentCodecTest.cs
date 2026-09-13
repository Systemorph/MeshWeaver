// <meshweaver>
// Id: Testing/Data/PointerSegmentCodecTest
// DisplayName: Testing/Data/PointerSegmentCodecTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data.Serialization;

/// <summary>
/// Pins <see cref="JsonSynchronizationStream.DecodePointerSegment"/> against the RFC 6901
/// ordering trap.
/// <para>
/// An entity id reaches this method as the ESCAPED pointer segment produced by
/// <c>CreatePointerFromSegments</c> (<c>~</c>→<c>~0</c>, <c>/</c>→<c>~1</c>). Decoding must undo
/// that in ONE pass. The previous implementation ran
/// <c>Replace("~0","~").Replace("~1","/")</c>, whose first pass MANUFACTURES a <c>~1</c> that the
/// second then eats: an id containing the literal text <c>~1</c> decoded to a slash, so the
/// resulting <see cref="EntityUpdate"/> carried the wrong id and the update landed on the wrong
/// entity (or nothing at all).
/// </para>
/// </summary>
public class PointerSegmentCodecTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Escape → decode must be the identity for every id shape.</summary>
    [MeshTheory]
    [MeshInlineData("plain")]
    [MeshInlineData("acme/Docs/One")]
    [MeshInlineData("has~tilde")]
    [MeshInlineData("has~1literal")]   // the case the two-pass decode corrupted
    [MeshInlineData("has~0literal")]
    [MeshInlineData("~1")]
    [MeshInlineData("~0")]
    [MeshInlineData("~01")]
    [MeshInlineData("a/b~c")]
    [MeshInlineData("")]
    [MeshInlineData("ümläut/😀")]
    public void EscapedId_DecodesBackToItself(string id)
    {
        // How the id is actually put on a pointer: JSON-encoded, then RFC 6901 escaped.
        var jsonEncoded = JsonSerializer.Serialize(id, Options);
        var escaped = jsonEncoded.Replace("~", "~0").Replace("/", "~1");

        var decoded = JsonSynchronizationStream.DecodePointerSegment(escaped, Options);

        Assert.Equal(id, Assert.IsType<JsonElement>(decoded).GetString());
    }

    /// <summary>The specific regression: <c>~01</c> is a literal <c>~1</c>, never a slash.</summary>
    [MeshFact]
    public void LiteralTildeOne_IsNotDecodedToASlash()
    {
        var decoded = JsonSynchronizationStream.DecodePointerSegment("\"a~01b\"", Options);
        Assert.Equal("a~1b", Assert.IsType<JsonElement>(decoded).GetString());
    }

    [MeshFact]
    public void NullSegment_DecodesToNull()
        => Assert.Null(JsonSynchronizationStream.DecodePointerSegment(null, Options));
}
