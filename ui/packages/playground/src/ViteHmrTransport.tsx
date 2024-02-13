import { PropsWithChildren, useMemo } from "react";
import { transportContext } from "@open-smc/application/src/transportContext";
import { HmrClientHub } from "./HmrClientHub";

import { layoutAddress, uiAddress } from "@open-smc/playground-server/src/contract";

export function ViteHmrTransport({children}: PropsWithChildren) {
    const value = useMemo(() => ({
        transportHub: new HmrClientHub(),
        layoutAddress: layoutAddress,
        uiAddress: uiAddress
    }), []);

    return (
        <transportContext.Provider value={value} children={children}/>
    )
}