import { makeHtml } from "@open-smc/sandbox/Html";
import { makeModalWindow, makeStack } from "@open-smc/sandbox/LayoutStack";
import { closeModalWindow, openModalWindow } from "../sampleApp";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { main } from "./main";
import { brandeisBlue, brightGray } from "@open-smc/application/colors";

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
