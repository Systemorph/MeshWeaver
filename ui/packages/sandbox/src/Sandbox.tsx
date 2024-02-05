import React, { Suspense, useMemo } from "react";
import { MessageHubBase } from "@open-smc/application/src/messageHub/MessageHubBase";
import { AddHub } from "@open-smc/application/src/messageHub/AddHub";
import { ControlStarter } from "@open-smc/application/src/ControlStarter";
import { ViewModelHub } from "./ViewModelHub";
import { SetAreaRequest } from "@open-smc/application/src/application.contract";
import { ControlDef } from "@open-smc/application/src/ControlDef";
import { layoutHubId } from "@open-smc/application/src/LayoutHub";
import { NotificationProvider } from "@open-smc/application/src/notifications/NotificationProvider";
import { InMemoryMessageRouter } from "@open-smc/sandbox/src/InMemoryMessageRouter";

interface Props {
    layoutHub?: MessageHubBase;
    path?: string;
    root?: ControlDef;
    log?: boolean;
}

export function Sandbox({root, layoutHub: originalLayoutHub, path}: Props) {
    const layoutHub =
        useMemo(() => originalLayoutHub ?? new DefaultApp(root), [root, originalLayoutHub]);

    return (
        <NotificationProvider>
            <InMemoryMessageRouter>
                <AddHub address={layoutHub} id={layoutHubId}>
                    <ControlStarter area={"app"} path={path}/>
                </AddHub>
            </InMemoryMessageRouter>
        </NotificationProvider>
    );
}

class DefaultApp extends ViewModelHub {
    constructor(root: ControlDef) {
        super();
        this.receiveMessage(SetAreaRequest, ({area}) => this.setArea(area, root));
    }
}