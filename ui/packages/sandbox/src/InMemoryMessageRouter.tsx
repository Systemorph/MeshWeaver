import React, { PropsWithChildren, useCallback, useMemo } from "react";
import { MessageHubBase } from "@open-smc/application/src/messageHub/MessageHubBase";
import { pack } from "@open-smc/application/src/MessageRouter";
import { down, makeLogger, up } from "@open-smc/application/src/logger";
import { messageRouterContext } from "@open-smc/application/src/MessageRouter";
import { MessageHub } from "@open-smc/application/src/messageHub/MessageHub";

export function InMemoryMessageRouter({children}: PropsWithChildren) {
    const log = true;

    const addHub = useCallback((address: unknown, hub: MessageHub) => {
        const hubPacked = (hub as MessageHubBase).pipe(pack(address, "UI"));
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