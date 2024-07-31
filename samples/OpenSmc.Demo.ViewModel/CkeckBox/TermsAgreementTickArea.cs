using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.CkeckBox;

public static class TermsAgreementTickArea
{
    public static object TermsTick(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack()
            .WithVerticalGap(16)
            .WithView(
                (a, _) =>
                    a.Bind(
                        new AgreementTick(false),
                        nameof(AgreementTick),
                        at => Controls.CheckBox("I agree with the Terms and Conditions", at.Signed)
                    )
            )
            .WithView(
                "SubmitAgreement",
                Controls.Button("Submit")
                    .WithIconStart(FluentIcons.Edit)
            )
        ;
}

internal record AgreementTick(bool Signed);
