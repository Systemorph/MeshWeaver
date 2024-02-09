import { createContext, PropsWithChildren, useCallback, useContext, useEffect, useMemo } from "react";
import { filter, map, Subscription } from "rxjs";
import { isEqual } from "lodash";
import { down, makeLogger, up } from "./logger";
import { useTransport } from "./transportContext";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";

const log = process.env.NODE_ENV === 'development';

type AddHub = (address: unknown, hub: MessageHub) => Subscription;

interface MessageRouterContext {
    addHub: AddHub;
    uiAddress: any;
}

export const messageRouterContext = createContext<MessageRouterContext>(null);

export function useMessageRouter() {
    return useContext(messageRouterContext);
}

export function MessageRouter({children}: PropsWithChildren) {
    const {transportHub, uiAddress} = useTransport();

    useEffect(() => {
        if (log) {
            const subscription = transportHub.subscribe(makeLogger(down));
            return () => subscription.unsubscribe();
        }
    }, [transportHub]);

    const addHub = useCallback(
        function addHub(address: unknown, hub: MessageHub) {
            const hubPacked = hub.pipe(pack(address, uiAddress));
            const subscription = hubPacked.subscribe(transportHub);

            subscription.add(
                transportHub.pipe(filterBySender(address))
                    // .pipe(tap(x => console.log(`Message ${x.id} reached target`)))
                    .subscribe(hub));

            if (log) {
                subscription.add(hubPacked.subscribe(makeLogger(up)));
            }

            return subscription;
        },
        [transportHub, uiAddress]
    );

    const value = useMemo(() => ({
        addHub,
        uiAddress
    }), [addHub, uiAddress]);

    return (
        <messageRouterContext.Provider value={value} children={children}/>
    );
}

const filterBySender = (address: unknown) => {
    const addressPojo = address && JSON.parse(JSON.stringify(address));

    return filter(({sender}: MessageDelivery) => {
        return isEqual(sender, addressPojo)
    });
}

export function pack(target: unknown, sender: unknown) {
    return map((delivery: MessageDelivery) => {
        return {
            target,
            ...delivery,
            sender
        }
    });
}