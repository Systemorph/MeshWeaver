import React, { PropsWithChildren, useCallback, useMemo } from "react";
import { MessageHub } from "@open-smc/application/messageHub/MessageHub";
import { pack } from "@open-smc/application/SignalrMessageRouter";
import { down, makeLogger, up } from "@open-smc/application/logger";
import { messageRouterContext } from "@open-smc/application/messageRouterContext";

export function InMemoryMessageRouter({children}: PropsWithChildren) {
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