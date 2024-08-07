using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// ActivityControl is a control that represents an activity in the system.
/// </summary>
/// <param name="User">UserInfo, for now populated with Email, DisplayName and Photo</param>
/// <param name="Title">Text</param>
/// <param name="Summary">Control</param>
/// <param name="View">Control of main body</param>
/// <param name="Menu">Control</param>
/// <param name="Options">Control</param>
public record ActivityControl(
    object User,
    object Title,
    object Summary,
    object View,
    string Color,
    DateTime Date,
    ImmutableList<MenuItemControl> Menu,
    ImmutableList<MenuItemControl> Options
) : UiControl<ActivityControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);
