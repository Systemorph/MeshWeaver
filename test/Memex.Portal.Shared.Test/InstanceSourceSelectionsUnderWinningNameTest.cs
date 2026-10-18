using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A local package source naming a repository the deployment's configuration already names is
/// dropped from the source list (the configured source wins), so its install-by-default selections
/// are spelled under the WINNING source's name — otherwise <c>reporting/Reporting</c> never matches
/// the listing under <c>Acme</c> and the package is never installed. A renaming only: the entries
/// still carry no deployment authority.
/// </summary>
public class InstanceSourceSelectionsUnderWinningNameTest
{
    [Fact]
    public void ALocalDuplicateOfAConfiguredRepository_SelectsUnderTheConfiguredName()
    {
        var local = new InstancePackageSourceContent
        {
            Repo = "https://github.com/acme/reporting",
            InstallByDefault = ["Reporting"],
        };
        var renamed = InstanceAutoRegistrationService.UnderWinningNames(
            [local], [("Acme", "https://github.com/acme/reporting.git")]);

        Assert.Equal(["Acme/Reporting"], InstancePackageSources.InstallEntries(renamed));
        // Control: without the mapping the entry names the local source, which the listing never carries.
        Assert.Equal(["reporting/Reporting"], InstancePackageSources.InstallEntries([local]));
    }

    [Fact]
    public void ALocalSourceOfAnotherRepository_KeepsItsOwnName()
    {
        var local = new InstancePackageSourceContent
        {
            Repo = "https://github.com/acme/billing",
            InstallByDefault = ["Billing"],
        };
        var renamed = InstanceAutoRegistrationService.UnderWinningNames(
            [local], [("Acme", "https://github.com/acme/reporting")]);

        Assert.Equal(["billing/Billing"], InstancePackageSources.InstallEntries(renamed));
    }
}
