import { Subscription } from "rxjs";
import { createContext, useContext } from "react";
import { MessageHub } from "./messageHub/MessageHub";

type AddHub = (address: unknown, hub: MessageHub) => Subscription;

interface MessageRouterContext {
    addHub: AddHub;
    uiAddress: any;
}

export const messageRouterContext = createContext<MessageRouterContext>(null);

export function useMessageRouter() {
    return useContext(messageRouterContext);
}