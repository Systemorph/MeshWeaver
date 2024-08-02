using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.Listbox;

public static class ListboxDemoArea
{
    public static LayoutDefinition AddListboxDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(BaseColorListArea.BaseColorList), BaseColorListArea.BaseColorList,
                options => options
                    .WithMenu(Controls.NavLink("Raw: Listbox Control", FluentIcons.Grid,
                        layout.ToHref(new(nameof(BaseColorListArea.BaseColorList)))))
        );
}
