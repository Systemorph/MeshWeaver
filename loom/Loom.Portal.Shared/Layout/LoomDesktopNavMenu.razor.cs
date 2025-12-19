using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Loom.Portal.Shared.Layout;

public partial class LoomDesktopNavMenu : ComponentBase
{
    public static Icon HomeIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Home()
            : new Icons.Regular.Size24.Home();

    public static Icon GraphIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.BranchFork()
            : new Icons.Regular.Size24.BranchFork();

    public static Icon OrganizationIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Building()
            : new Icons.Regular.Size24.Building();

    public static Icon PersonIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Person()
            : new Icons.Regular.Size24.Person();
}
