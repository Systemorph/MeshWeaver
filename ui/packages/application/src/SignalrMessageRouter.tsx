import { PropsWithChildren, useEffect, useMemo, useState } from "react";
import { MessageHub } from "./messageHub/MessageHub";
import { MessageDelivery, SignalrHub } from "./SignalrHub";
import { filter, map } from "rxjs";
import { isEqual } from "lodash";
import { SignalrConnectionProvider, useConnectionSelector } from "./SignalrConnectionProvider";
import { down, makeLogger, up } from "./logger";
import { UiAddress } from "./application.contract";
import { messageRouterContext } from "./messageRouterContext";

const log = process.env.NODE_ENV === 'development';

export function SignalrMessageRouter({children}: PropsWithChildren) {
    return (
        <SignalrConnectionProvider>
            <SignalrMessageRouterInner children={children}/>
        </SignalrConnectionProvider>
    );
}

function SignalrMessageRouterInner({children}: PropsWithChildren) {
    const appId = useConnectionSelector("appId");
    const connection = useConnectionSelector("connection");
    const [signalrHub] = useState(new SignalrHub(connection));
    const uiAddress = useMemo(() => new UiAddress(appId), [appId]);

    useEffect(() => {
        if (log) {
            const subscription = signalrHub.subscribe(makeLogger(down));
            return () => subscription.unsubscribe();
        }
    }, [signalrHub]);

    const addHub = useMemo(
        () =>
            function addHub(address: unknown, hub: MessageHub) {
                const hubPacked = hub.pipe(pack(address, uiAddress));
                const subscription = hubPacked.subscribe(signalrHub);

                subscription.add(
                    signalrHub.pipe(filterBySender(address))
                        // .pipe(tap(x => console.log(`Message ${x.id} reached target`)))
                        .subscribe(hub));

                if (log) {
                    subscription.add(hubPacked.subscribe(makeLogger(up)));
                }

                return subscription;
            },
        [signalrHub, uiAddress]
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