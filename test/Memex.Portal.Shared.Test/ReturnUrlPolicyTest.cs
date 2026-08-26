using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

public class ReturnUrlPolicyTest
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/Doc/Architecture", "/Doc/Architecture")]
    [InlineData("/a?b=c#d", "/a?b=c#d")]
    public void Local_paths_are_honoured_and_empty_falls_back(string? input, string expected)
        => Assert.Equal(expected, ReturnUrlPolicy.Sanitize(input));

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example/protocol-relative")]
    [InlineData("/\\evil.example/backslash-variant")]
    [InlineData("evil.example")]
    [InlineData("javascript:alert(1)")]
    public void Anything_not_local_falls_back_to_root(string input)
        => Assert.Equal("/", ReturnUrlPolicy.Sanitize(input));
}
