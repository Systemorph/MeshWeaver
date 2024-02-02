import React, { Suspense, useMemo } from "react";
import { MessageHubBase } from "@open-smc/application/messageHub/MessageHubBase";
import { AddHub } from "@open-smc/application/messageHub/AddHub";
import { ControlStarter } from "@open-smc/application/ControlStarter";
import { ViewModelHub } from "./ViewModelHub";
import { SetAreaRequest } from "@open-smc/application/application.contract";
import { ControlDef } from "@open-smc/application/ControlDef";
import { layoutHubId } from "@open-smc/application/LayoutHub";
import { NotificationProvider } from "@open-smc/application/notifications/NotificationProvider";
import { InMemoryMessageRouter } from "@open-smc/sandbox/InMemoryMessageRouter";

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