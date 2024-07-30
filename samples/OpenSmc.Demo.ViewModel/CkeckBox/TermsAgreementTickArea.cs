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
                "AgreeWithTerms",
                Controls.Html("I agree with the Terms and Conditions")
                //Controls.CheckBox("I agree with the Terms and Conditions", false)
            )
            .WithView(
                "SubmitAgreement",
                Controls.Button("Submit")
                    .WithIconStart(FluentIcons.Edit)
            )
        ;
}
