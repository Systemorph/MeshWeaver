﻿namespace MeshWeaver.Layout;

public record LayoutSkin : Skin<LayoutSkin>;

public record HeaderSkin : Skin<HeaderSkin>;

public record FooterSkin : Skin<FooterSkin>;

public record SpacerControl() : UiControl<SpacerControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);

public record TabSkin(object Label) : Skin<TabSkin>;