// <meshweaver>
// Id: Testing/MemexPortalShared/ReturnUrlPolicyTest
// DisplayName: Testing/MemexPortalShared/ReturnUrlPolicyTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using Memex.Portal.Shared.Authentication;

public class ReturnUrlPolicyTest
{
    [MeshTheory]
    [MeshInlineData(null, "/")]
    [MeshInlineData("", "/")]
    [MeshInlineData("   ", "/")]
    [MeshInlineData("/", "/")]
    [MeshInlineData("/Doc/Architecture", "/Doc/Architecture")]
    [MeshInlineData("/a?b=c#d", "/a?b=c#d")]
    public void Local_paths_are_honoured_and_empty_falls_back(string? input, string expected)
        => Assert.Equal(expected, ReturnUrlPolicy.Sanitize(input));

    [MeshTheory]
    [MeshInlineData("https://evil.example/phish")]
    [MeshInlineData("http://evil.example")]
    [MeshInlineData("//evil.example/protocol-relative")]
    [MeshInlineData("/\\evil.example/backslash-variant")]
    [MeshInlineData("evil.example")]
    [MeshInlineData("javascript:alert(1)")]
    public void Anything_not_local_falls_back_to_root(string input)
        => Assert.Equal("/", ReturnUrlPolicy.Sanitize(input));
}
