import { ClickedEvent } from "@open-smc/application/src/contract/application.contract";
import { MainWindowStack, makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { uiAddress } from "../contract";

export class PlaygroundWindow extends MainWindowStack {
    constructor() {
        super();

        this.withAddress("PlaygroundWindow");
        this.withSideMenu(this.makeSideMenu());

        this.withMessageHandler(ClickedEvent, ({message}) => {
            this.sendMessage(message.payload, uiAddress);
        });
    }

    private makeSideMenu() {
        return makeStack()
            .withAddress("SideMenu")
            .withView(
                makeMenuItem()
                    .withAddress("MenuItemButton")
                    // .withSkin("LargeIcon")
                    .withTitle("MenuItem")
                    // .withColor("#0171ff")
                    // .withIcon("sm-systemorph-fill")
                    .withClickMessage({address: this.address, message: new ClickedEvent("1", "Hello")})
            );
    }
}