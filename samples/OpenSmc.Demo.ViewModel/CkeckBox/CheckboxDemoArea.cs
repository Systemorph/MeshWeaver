using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.CkeckBox;

public static class CheckboxDemoArea
{
    public static LayoutDefinition AddCheckboxDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(TermsAgreementTickArea.TermsTick), TermsAgreementTickArea.TermsTick,
                options => options
                    .WithMenu(Controls.NavLink("Raw: Checkbox Control", FluentIcons.Person,
                        layout.ToHref(new(nameof(TermsAgreementTickArea.TermsTick)))))
        );
}
