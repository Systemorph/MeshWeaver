import { PropsWithChildren, useMemo } from "react";
import { SignalrConnectionProvider, useConnectionSelector } from "./SignalrConnectionProvider";
import { SignalrHub } from "./signalr/SignalrHub";
import { LayoutAddress, UiAddress } from "@open-smc/layout/src/contract/application.contract";
import { transportContext } from "./transportContext";

export function SignalrTransport({children}: PropsWithChildren) {
    return (
        <SignalrConnectionProvider>
            <SignalrTransportInner children={children}/>
        </SignalrConnectionProvider>
    )
}

function SignalrTransportInner({children}: PropsWithChildren) {
    const appId = useConnectionSelector("appId");
    const connection = useConnectionSelector("connection");
    const transportHub = useMemo(() => new SignalrHub(connection), [connection]);
    const uiAddress = useMemo(() => new UiAddress(appId), [appId]);
    const layoutAddress = useMemo(() => new LayoutAddress(appId), [appId]);

    const value = useMemo(() => ({
        transportHub,
        layoutAddress,
        uiAddress
    }), [transportHub, layoutAddress, uiAddress]);

    return (
        <transportContext.Provider value={value} children={children}/>
    )
}