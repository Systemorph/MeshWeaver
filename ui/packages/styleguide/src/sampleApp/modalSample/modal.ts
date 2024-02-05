import { makeHtml } from "@open-smc/sandbox/src/Html";
import { makeModalWindow, makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { closeModalWindow, openModalWindow } from "../sampleApp";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { main } from "./main";
import { brandeisBlue, brightGray } from "@open-smc/application/src/colors";

export const header = makeHtml('<h2>Sign off</h2>');

export const footer = makeStack()
    .withView(
        makeMenuItem()
            .withTitle("Sign off")
            .withColor(brandeisBlue)
            .withIcon("check")
            .withClickAction(()=>openModalWindow(makeHtml("<b>Test</b>")))
    )
    .withView(makeMenuItem()
        .withTitle("Cancel")
        .withColor(brightGray)
        .withClickAction(closeModalWindow)
    )
    .withSkin("HorizontalPanel")
    .withFlex(style=>style.withGap("12px"));

export const modal = makeModalWindow()
    .withHeader(header)
    .withMain(main)
    .withFooter(footer);
