using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The first global administrator is SEEDED with no learning path; an ordinary user still is —
/// policy <c>first-admin-no-learning-path</c> (<c>Doc/Architecture/PolicyNotProse</c>). They are
/// setting the instance up, not learning it.
///
/// <para>These are the pure halves: the seed function and the request's default. What the seed
/// does on a LIVE mesh — and, the case that matters, what a second onboarding of the same user
/// does NOT do to pins added in between — is <see cref="OnboardingPinsAreSeededOnceTest"/>.</para>
/// </summary>
public class FirstAdministratorHasNoLearningPathTest
{
    [Fact]
    public void TheFirstAdministratorIsPinnedToNothing()
    {
        Assert.Empty(UserOnboardingDefaults.PinnedPathsFor(isPlatformBootstrap: true));
    }

    [Fact]
    public void AnOrdinaryUserKeepsTheFourDocSections()
    {
        Assert.Equal(
            ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"],
            UserOnboardingDefaults.PinnedPathsFor(isPlatformBootstrap: false));
    }

    [Fact]
    public void TheRequestDefaultsToAnOrdinaryUser()
    {
        // Every existing caller constructs the request without the flag, and every one of them
        // creates an ordinary user: the default must not silently strip anybody's pins.
        Assert.False(new UserOnboardingRequest("u", "u@x.example").IsPlatformBootstrap);
    }
}
