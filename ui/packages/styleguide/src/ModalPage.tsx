import { Sandbox } from "@open-smc/sandbox/Sandbox";
import { sampleApp } from "./sampleApp/sampleApp";
import { makeModalWindow, makeStack } from "@open-smc/sandbox/LayoutStack";
import {
    ModalDef, promotionFailed,
    promotionFinishedSuccessfully, promotionFinishedSuccessWithWarnings,
    promotionStart,
    promotionValidated
} from "./sampleApp/orsaProcessSteps";
import { noop } from "lodash";

const start = getModalWindow(promotionStart(noop, noop));
const validated = getModalWindow(promotionValidated(noop, noop));
const success = getModalWindow(promotionFinishedSuccessfully(noop, noop));
const successWithWarnings = getModalWindow(promotionFinishedSuccessWithWarnings(noop, noop));
const failed = getModalWindow(promotionFailed(noop, noop));

const stack = makeStack()
    .withSkin("VerticalPanel")
    .withStyle(style => style.withRowGap("20px"))
    .withView(start)
    .withView(validated)
    .withView(success)
    .withView(successWithWarnings)
    .withView(failed)
    .build();

export function ModalPage() {
    return (
        <Sandbox root={stack} log={true} />
    );
}

function getModalWindow(modalDef: ModalDef) {
    const {header, main, footer} = modalDef;

    const modal = makeModalWindow()
        .withStyle(style =>
            style.withBorder("1px solid #ccc")
                .withBorderRadius("6px")
                .withWidth("600px")
        );

    if (header) {
        modal.withHeader(header);
    }

    if (main) {
        modal.withMain(main);
    }

    if (footer) {
        modal.withFooter(footer);
    }

    return modal;
}