import React, { useCallback, Suspense, useMemo, PropsWithChildren, useState } from "react";
import { messageRouterContext } from "@open-smc/application/messageRouterContext";
import { MessageHub } from "@open-smc/application/messageHub/MessageHub";
import { down, makeLogger, up } from "@open-smc/application/logger";
import { AddHub } from "@open-smc/application/messageHub/AddHub";
import { ControlStarter } from "@open-smc/application/ControlStarter";
import { useNotification } from "rc-notification";
import { notificationConfig } from "@open-smc/application/notificationConfig";
import { makeToastApi } from "@open-smc/application/makeToastApi";
import { makeAppStore } from "@open-smc/application/appStore";
import { appContext } from "@open-smc/application/App";
import { ViewModelHub } from "./ViewModelHub";
import { SetAreaRequest } from "@open-smc/application/application.contract";
import { ControlDef } from "@open-smc/application/ControlDef";
import { layoutHubId } from "@open-smc/application/LayoutHub";
import { pack } from "@open-smc/application/SignalrMessageRouter";

interface Props {
    layoutHub?: MessageHub;
    path?: string;
    root?: ControlDef;
    log?: boolean;
}

export function Sandbox({root, layoutHub: originalLayoutHub, path}: Props) {
    const layoutHub =
        useMemo(() => originalLayoutHub ?? new DefaultApp(root), [root, originalLayoutHub]);

    return (
        <Suspense fallback={<div>Loading...</div>}>
            <InMemoryRouter>
                <AddHub address={layoutHub} id={layoutHubId}>
                    <SandboxApp path={path}/>
                </AddHub>
            </InMemoryRouter>
        </Suspense>
    );
}

class DefaultApp extends ViewModelHub {
    constructor(root: ControlDef) {
        super();
        this.receiveMessage(SetAreaRequest, ({area}) => this.setArea(area, root));
    }
}

interface SandboxAppProps {
    path: string;
}

function SandboxApp({path}: SandboxAppProps) {
    const [notificationApi, notificationContainer] = useNotification(notificationConfig);
    const [toastApi] = useState(makeToastApi(notificationApi));
    const [store] = useState(makeAppStore);

    const value = useMemo(() => ({
        store,
        toastApi,
    }), [toastApi, store]);

    return (
        <appContext.Provider value={value}>
            <ControlStarter area={"app"} path={path}/>
            {notificationContainer}
        </appContext.Provider>
    );
}

export function InMemoryRouter({children}: PropsWithChildren) {
    const log = true;

    const addHub = useCallback((address: unknown, hub: MessageHub) => {
        const hubPacked = hub.pipe(pack(address, "UI"));
        const modelHub = new MessageHub();
        const subscription = (address as MessageHub).exposeAs(modelHub);
        subscription.add(modelHub.subscribe(hub));
        subscription.add(hubPacked.subscribe(modelHub));

        if (log) {
            subscription.add(hubPacked.subscribe(makeLogger(up)));
            subscription.add(modelHub.subscribe(makeLogger(down)));
        }

        return subscription;
    }, [log]);

    const value = useMemo(() => {
        return {
            addHub,
            uiAddress: null
        }
    }, [addHub]);

    return (
        <messageRouterContext.Provider value={value} children={children}/>
    );
}
