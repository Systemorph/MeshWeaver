import { createContext, useContext } from "react";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";

interface TransportContextType {
    transportHub: MessageHub;
    layoutAddress: unknown;
    uiAddress: unknown;
}

export const transportContext = createContext<TransportContextType>(null);

export function useTransport() {
    return useContext(transportContext);
}