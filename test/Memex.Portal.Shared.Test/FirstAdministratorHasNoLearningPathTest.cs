using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The first global administrator is pinned to no learning path; an ordinary user still is.
///
/// <para>Maintainer, 2026-09-21, onboarding on partnerre.meshweaver.cloud: <i>"the learning path i
/// don't need for first onboarding of global admin."</i> They are setting the instance up, not
/// learning it.</para>
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
