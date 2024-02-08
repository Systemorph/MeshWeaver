import { PropsWithChildren, useMemo } from "react";
import { transportContext } from "@open-smc/application/src/transportContext";
import { HmrClientHub } from "./HmrClientHub";

export function ViteHmrTransport({children}: PropsWithChildren) {
    const value = useMemo(() => ({
        transportHub: new HmrClientHub(),
        layoutAddress: "Layout",
        uiAddress: "Ui"
    }), []);

    return (
        <transportContext.Provider value={value} children={children}/>
    )
}