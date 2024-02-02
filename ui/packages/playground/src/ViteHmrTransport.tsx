import { PropsWithChildren, useMemo } from "react";
import { transportContext } from "@open-smc/application/src/transportContext";
import { ViteHmrHub } from "./ViteHmrHub.tsx";

export function ViteHmrTransport({children}: PropsWithChildren) {
    const value = useMemo(() => ({
        transportHub: new ViteHmrHub(),
        layoutAddress: "Layout",
        uiAddress: "Ui"
    }), []);

    return (
        <transportContext.Provider value={value} children={children}/>
    )
}