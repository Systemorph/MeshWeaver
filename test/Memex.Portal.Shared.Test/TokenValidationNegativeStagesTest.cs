using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The stage a rejected bearer token is logged under tells the truth about the ROW.
///
/// <para><b>Why the log line is the subject.</b> A 401 from the bearer path is a definitive
/// verdict, and the investigation of #5074 rested its whole inference on the stage the validator
/// logs: <c>index-not-found</c> / <c>token-not-found</c> were read as "the row was deleted", which
/// in the OAuth path can only be the one-credential-per-client supersede. That reading is sound
/// ONLY if <c>-not-found</c> is emitted exclusively when the store answered that no row exists.
/// It was not: a row that IS there but whose content cannot be read as a token record took the
/// same branch and printed <i>"no row at {Path}"</i> — sending the reader after a delete that
/// never happened. The two facts now carry two stages, <c>-not-found</c> and <c>-unreadable</c>.</para>
///
/// <para><b>What does NOT change.</b> Both are definitive negatives — the store was read, and a row
/// carrying no token record authenticates nobody — so the verdict stays
/// <see cref="TokenValidationStatus.Invalid"/> in both cases; <see cref="TokenValidationStatus.Unavailable"/>
/// remains reserved for a store that could not be read at all. The assertions pin that too.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the two negatives fold back into one line:
/// <see cref="ARowThatExistsButCarriesNoTokenRecord_IsLoggedUnreadable_NotNotFound"/> goes red on
/// the pre-change code (it printed <c>index-not-found</c> for that row), while
/// <see cref="AnAbsentRow_IsLoggedNotFound_NeverUnreadable"/> is the control in the other
/// direction — an implementation that stamped every negative <c>-unreadable</c> would pass the
/// first and fail this one, so no constant satisfies the pair.</para>
/// </summary>
public class TokenValidationNegativeStagesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CapturingLogger<ApiTokenService> Log { get; } = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private ApiTokenService Service() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Storage,
        Log);

    /// <summary>A raw token nobody minted, in the wire shape the validator accepts.</summary>
    private static string UnmintedRawToken() =>
        "mw_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    [Fact]
    public async Task ARowThatExistsButCarriesNoTokenRecord_IsLoggedUnreadable_NotNotFound()
    {
        var rawToken = UnmintedRawToken();
        var prefix = ApiTokenService.HashToken(rawToken)[..12];
        var indexPath = $"ApiToken/{prefix}";

        // The index row EXISTS at the address the validator reads first, but nothing in it is a
        // token record: neither an ApiTokenIndex nor a legacy ApiToken, typed or as JSON.
        var written = await Storage
            .Write(new MeshNode(prefix, "ApiToken")
            {
                Name = "not a token record",
                NodeType = "ApiToken",
                State = MeshNodeState.Active,
                Content = "not a token record",
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        written.Should().NotBeNull("the row must be in the store before validation reads it");

        var verdict = await Service().Validate(rawToken)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        verdict.Status.Should().Be(
            TokenValidationStatus.Invalid,
            "the store WAS read and the row carries no token record — a definitive negative, never Unavailable");

        var warnings = Log.Lines(LogLevel.Warning);
        var forThisToken = warnings.Where(l => l.Contains(prefix)).ToArray();
        forThisToken.Should().ContainSingle(
            l => l.Contains("index-unreadable"),
            "a row that exists but cannot be typed is its own fact, logged under its own stage");
        forThisToken.Single(l => l.Contains("index-unreadable")).Should()
            .Contain($"row at {indexPath} exists")
            .And.Contain("was NOT deleted",
                "the line must say the opposite of what -not-found says, because the opposite is what happened")
            .And.Contain("String",
                "naming the content's runtime type is what makes the next occurrence attributable");
        forThisToken.Should().NotContain(
            l => l.Contains("-not-found") || l.Contains("no row at"),
            "'no row' is the claim an investigation reads as 'the row was deleted', and here the row is there");
    }

    [Fact]
    public async Task AnAbsentRow_IsLoggedNotFound_NeverUnreadable()
    {
        var rawToken = UnmintedRawToken();
        var prefix = ApiTokenService.HashToken(rawToken)[..12];

        var verdict = await Service().Validate(rawToken)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        verdict.Status.Should().Be(
            TokenValidationStatus.Invalid,
            "an absent row is a verdict, not a failure to reach one");

        var forThisToken = Log.Lines(LogLevel.Warning).Where(l => l.Contains(prefix)).ToArray();
        forThisToken.Should().ContainSingle(
            l => l.Contains("index-not-found") && l.Contains($"no row at ApiToken/{prefix}"),
            "an absent row keeps the stage every investigation already knows how to read");
        forThisToken.Should().NotContain(
            l => l.Contains("-unreadable"),
            "the unreadable stage is reserved for a row that exists — stamping it on absence would "
            + "make the split meaningless");
    }
}
