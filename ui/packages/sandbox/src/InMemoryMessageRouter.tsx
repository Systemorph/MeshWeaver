import React, { PropsWithChildren, useCallback, useMemo } from "react";
import { MessageHubBase } from "@open-smc/application/messageHub/MessageHubBase";
import { pack } from "@open-smc/application/MessageRouter";
import { down, makeLogger, up } from "@open-smc/application/logger";
import { messageRouterContext } from "@open-smc/application/MessageRouter";

export function InMemoryMessageRouter({children}: PropsWithChildren) {
    const log = true;

    const addHub = useCallback((address: unknown, hub: MessageHubBase) => {
        const hubPacked = hub.pipe(pack(address, "UI"));
        const modelHub = new MessageHubBase();
        const subscription = (address as MessageHubBase).exposeAs(modelHub);
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