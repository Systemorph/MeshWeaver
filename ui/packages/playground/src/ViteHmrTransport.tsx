import { PropsWithChildren, useEffect, useMemo } from "react";
import { transportContext } from "@open-smc/application/src/transportContext";
import { MessageHubBase } from "@open-smc/application/src/messageHub/MessageHubBase.ts";

export function ViteHmrTransport({children}: PropsWithChildren) {
    useEffect(() => {
        if (import.meta.hot) {
            import.meta.hot.send('my:from-client', { msg: 'Hey!' })
        }
    }, []);

    const value = useMemo(() => ({
        transportHub: new MessageHubBase(),
        layoutAddress: "Layout",
        uiAddress: "Ui"
    }), []) as any;

    return (
        <transportContext.Provider value={value} children={children}/>
    )
}