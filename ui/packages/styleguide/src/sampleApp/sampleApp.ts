import { makeSmappWindow, makeStack } from "@open-smc/sandbox/LayoutStack";
import { sideMenu } from "./sideMenu";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { mainWindowAreas } from "@open-smc/application/controls/MainWindow";
import {toolbar} from "./toolbar";
import {ModalOptions} from '@open-smc/application/controls/MainWindow';
import { ControlBuilderBase } from "@open-smc/sandbox/ControlBase";

export const sampleApp = makeSmappWindow()
    .withSideMenu(sideMenu)
    .withToolbar(toolbar)
    .build();

export function openContextPanel(header: ControlBuilderBase, content: ControlBuilderBase) {
    sampleApp.setArea(
        mainWindowAreas.contextMenu,
        makeStack()
            .withView(
                makeStack()
                    .withView(header)
                    .withView(
                        makeMenuItem()
                            .withIcon("close")
                            .withClickAction(closeContextPanel)
                    )
                    .withSkin("HorizontalPanel")
                    .withStyle(style => style.withJustifyContent("space-between"))
            )
            .withView(content)
            .withSkin("ContextMenu")
            .build()
    );
}

export function openModalWindow(content: ControlBuilderBase, options?: ModalOptions) {
    sampleApp.setArea(
        mainWindowAreas.modal,
        content.build(),
        options
    );
}

export function closeModalWindow() {
    sampleApp.setArea(mainWindowAreas.modal, null);
}

export function closeContextPanel() {
    sampleApp.setArea(mainWindowAreas.contextMenu, null);
}
