import { PropsWithChildren, useEffect, useMemo, useState } from "react";
import { MessageHub } from "./messageHub/MessageHub";
import { MessageDelivery, SignalrHub } from "./SignalrHub";
import { filter, map } from "rxjs";
import { isEqual } from "lodash";
import { useConnection, useConnectionStatus } from "./Connection";
import { down, makeLogger, up } from "./logger";
import { UiAddress } from "./application.contract";
import { messageRouterContext } from "./messageRouterContext";

interface Props {
    log?: boolean;
}

export function SignalrMessageRouter({log, children}: PropsWithChildren & Props) {
    const connection = useConnection();
    const {appId} = useConnectionStatus();
    const [signalr] = useState(new SignalrHub(connection));

    const uiAddress = useMemo(() => new UiAddress(appId), [appId]);

    useEffect(() => {
        if (log) {
            const subscription = signalr.subscribe(makeLogger(down));
            return () => subscription.unsubscribe();
        }
    }, [signalr, log]);

    const addHub = useMemo(
        () =>
            function addHub(address: unknown, hub: MessageHub) {
                const hubPacked = hub.pipe(pack(address, uiAddress));
                const subscription = hubPacked.subscribe(signalr);

                subscription.add(
                    signalr.pipe(filterBySender(address))
                        // .pipe(tap(x => console.log(`Message ${x.id} reached target`)))
                        .subscribe(hub));

                if (log) {
                    subscription.add(hubPacked.subscribe(makeLogger(up)));
                }

                return subscription;
            },
        [signalr, log, uiAddress]
    );

    const value = useMemo(() => {
        return {
            addHub,
            uiAddress
        }
    }, [addHub, uiAddress]);

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