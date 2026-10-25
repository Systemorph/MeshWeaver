# MeshWeaver.ShortGuid

## Overview
A compact, URL-safe string form of a GUID: the 16 bytes of `Guid.ToByteArray()` as Base64 with
`+` → `-`, `/` → `_` and the `==` padding dropped — always 22 characters.

Usage:
```csharp
var stringGuid = guid.AsString() // 22-character URL-safe string; Guid.Empty → ""
var guid = stringGuid.AsGuid()   // back to the Guid; null or "" → Guid.Empty
```

## The format is frozen
These ids are persisted, so the encoding must never change. The project has no package dependency:
it used to wrap `CSharpVitamins.ShortGuid` 2.0.0 and now implements the same format in-house, byte
for byte. Decoding keeps that package's strict rules:

- the input must be exactly 22 characters — a 36-character `Guid` string is rejected with an
  `ArgumentException`, as is input that decodes to fewer than 16 bytes;
- input that is not Base64, or that does not re-encode to exactly the same string (standard-alphabet
  `+`/`/`, non-canonical trailing bits), is rejected with a `FormatException`.

`ShortGuidEncodingVectorsTest` in `test/MeshWeaver.Messaging.Hub.Test` pins the vectors the original
package produced; a change there is a breaking change to stored data.

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
