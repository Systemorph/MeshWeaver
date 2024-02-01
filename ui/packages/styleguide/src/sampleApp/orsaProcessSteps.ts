import { makeHtml } from "@open-smc/sandbox/Html";
import { makeModalWindow, makeStack } from "@open-smc/sandbox/LayoutStack";
import { makeIcon } from "@open-smc/sandbox/Icon";
import { makeItemTemplate } from "@open-smc/sandbox/ItemTemplate";
import { makeCheckbox } from "@open-smc/sandbox/Checkbox";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";
import { v4 } from "uuid";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { brandeisBlue, brightGray } from "@open-smc/application/colors";
import { closeModalWindow, openModalWindow } from "./sampleApp";
import { ControlBuilderBase } from "@open-smc/sandbox/ControlBase";

type PromotionDialogFactory = (next: () => void, finish: () => void) => ModalDef;

export interface ModalDef {
    header?: ControlBuilderBase;
    main?: ControlBuilderBase;
    footer?: ControlBuilderBase;
}

export function makePromotionDialogs(...dialogs: PromotionDialogFactory[]) {
    function get(index: number) {
        return () => {
            const isLast = index === dialogs.length - 1;

            const next = isLast ? null : get(index + 1);

            const {header, main, footer} = dialogs[index](next, closeModalWindow);

            const modal = makeModalWindow();

            if (header) {
                modal.withHeader(header);
            }

            if (main) {
                modal.withMain(main);
            }

            if (footer) {
                modal.withFooter(footer);
            }

            openModalWindow(modal);
        }
    }

    return get(0);
}

export const promotionStart: PromotionDialogFactory = (next, finish) => {
    return {
        header: makeHtml("<h2>Orsa Estimates Process</h2>"),
        main: makeDialogContent(
            logsIconXXL,
            makeVerticalStack()
                .withView(
                    makeHtml(`
                            <p>You will be changing the following data packs from Submission to Review</p>
                        `)
                )
                .withView(
                    makeItemTemplate()
                        .withSkin("VerticalPanel")
                        .withView(
                            makeCheckbox()
                                .withId(makeBinding("item.id"))
                                .withLabel(makeBinding("item.label"))
                                .withData(makeBinding("item.data"))
                                .build()
                        )
                        .withData(
                            [
                                {
                                    id: v4(),
                                    data: true,
                                    label: "Base case"
                                },
                                {
                                    id: v4(),
                                    data: true,
                                    label: "ORSA1"
                                }
                            ]
                        )
                )
            ),
        footer: makeHorizontalStack()
            .withView(
                makeMenuItem()
                    .withTitle("Sign off")
                    .withColor(brandeisBlue)
                    .withIcon("check")
                    .withClickAction(next)
            )
            .withView(makeMenuItem()
                .withTitle("Cancel")
                .withColor(brightGray)
                .withClickAction(closeModalWindow)
            )
    }
}

export const promotionValidated: PromotionDialogFactory = (next, finish) => ({
    header: makeHtml("<h2>Orsa Estimates Process</h2>"),
    main: makeHtml(`<p>The change of BaseCase, ORSA1 from Submission to Review has been validated</p>`),
    footer: makeHorizontalStack()
        .withView(
            makeMenuItem()
                .withTitle("Continue")
                .withColor(brandeisBlue)
                .withIcon("check")
                .withClickAction(next)
        )
        .withView(
            makeMenuItem()
                .withTitle("Cancel")
                .withColor(brightGray)
                .withClickAction(closeModalWindow)
        )
});

export const promotionFinishedSuccessfully: PromotionDialogFactory = (next, finish) => ({
    header: makeHtml("<h2>Promotion succeeded</h2>"),
    main: makeDialogContent(successIconXXL, makeHtml(`<p>Promotion finished successfully</p>`))
        .withView(makeCloseMenuItem().withClickAction(finish)),
});

export const promotionFinishedSuccessWithWarnings: PromotionDialogFactory = (next, finish) => ({
        header: makeHtml("<h2>Promotion succeeded</h2>"),
        main: makeDialogContent(successIconXXL, makeHtml(`<p>Promotion finished successfully</p>`))
            .withView(
                makeMessages(
                    infoIconXL,
                    makeHtml(`<div class="messages">
                            <h4>Information</h4>
                            <p>Something went wrong</p>
                            </div>
                        `)
                ).withStyle(
                    style => style.withGap("40px").withAlignItems('flex-start').withMargin("0 56px 0 156px")
                )
            )
            .withView(
                makeCloseMenuItem()
                    .withClickAction(finish)
            )
    }
);

export const promotionFailed: PromotionDialogFactory = (next, finish) => ({
    header: makeHtml("<h2>Promotion failed</h2>"),
    main: makeDialogContent(errorIconXXL, makeHtml(`<p>Promotion from submission to review has failed</p>`))
        .withView(
            makeMessages(
                errorIconXL,
                makeHtml(`<div class="messages">
                            <h4>Errors</h4>
                            <p>Promotion didn't finish successfully</p>
                            <p>Promotion didn't finish successfully</p>
                            </div>
                        `)
            ).withStyle(
                    style => style.withGap("40px").withAlignItems('flex-start').withMargin("0 56px 0 156px")
                )
        )
        .withView(
            makeMessages(
                warningIconXL,
                makeHtml(`<div class="messages">
                            <h4>Warnings</h4>
                            <p>Something went wrong</p>
                            </div>
                        `)
            ).withStyle(
                    style => style.withGap("40px").withAlignItems('flex-start').withMargin("0 56px 0 156px")
                )

        )
        .withView(
            makeCloseMenuItem()
                .withClickAction(finish)
        ),
});

export const subToRevSuccess = makePromotionDialogs(
    promotionStart,
    promotionValidated,
    promotionFinishedSuccessfully
);

export const subToRevSuccessWithWarnings = makePromotionDialogs(
    promotionStart,
    promotionValidated,
    promotionFinishedSuccessWithWarnings
);

export const subToRevError = makePromotionDialogs(
    promotionStart,
    promotionValidated,
    promotionFailed
);

const successIconXXL = makeIcon("check")
    .withSize("XXL")
    .withColor("#03CB5D")
    .withBackground(true)
    .withBorderRadius("circle");

const warningIconXL = makeIcon("alert")
    .withSize("XL")
    .withColor("#F3B200")
    .withBackground(true)
    .withBorderRadius("circle");

const infoIconXL = makeIcon("info")
    .withSize("XL")
    .withColor("#4D9CFF")
    .withBackground(true)
    .withBorderRadius("circle");

const errorIconXXL = makeIcon("alert")
    .withSize("XXL")
    .withColor("#F75435")
    .withBackground(true)
    .withBorderRadius("circle");

const errorIconXL = makeIcon("alert")
    .withSize("XL")
    .withColor("#F75435")
    .withBackground(true)
    .withBorderRadius("circle");

const logsIconXXL = makeIcon("logs")
    .withColor("#874170")
    .withSize("XXL")
    .withBackground(true)
    .withBorderRadius("circle");

const makeHorizontalStack = () => makeStack().withSkin("HorizontalPanel");
const makeVerticalStack = () => makeStack().withSkin("VerticalPanel");
const makeCloseMenuItem = () => makeMenuItem()
    .withTitle("Close")
    .withIcon("close")
    .withColor("#EDEDED");

function makeDialogContent(
    icon: ControlBuilderBase,
    main: ControlBuilderBase
) {
    return makeVerticalStack()
        .withStyle(
            style => style.withRowGap("32px")
        )
        .withView(
            makeHorizontalStack()
                .withStyle(
                    style => style.withGap("40px").withMargin("0 56px 0 100px")
                )
                .withView(icon)
                .withView(main)
        );
}

function makeMessages(icon: ControlBuilderBase, content: ControlBuilderBase) {
    return makeHorizontalStack()
        .withStyle(style => style.withAlignItems("flex-start"))
        .withView(icon)
        .withView(content)
}
