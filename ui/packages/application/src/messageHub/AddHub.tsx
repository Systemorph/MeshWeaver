import { createContext, PropsWithChildren, useContext, useEffect, useState } from "react";
import { ConnectToHubRequest } from "../application.contract";
import { MessageHubBase } from "./MessageHubBase";
import { useMessageRouter } from "../MessageRouter";

const hubContext = createContext<MessageHubFinder>(null);

type MessageHubFinder = (id: string) => MessageHubBase;

export const useMessageHub = (id?: string) => {
    return useContext(hubContext)?.(id);
};

interface MessageHubProps {
    address: any;
    id?: string;
}

export function AddHub({address, id, children}: PropsWithChildren<MessageHubProps>) {
    const parentContextValue = useContext(hubContext);
    const {addHub, uiAddress} = useMessageRouter();
    const [value, setValue] = useState<MessageHubFinder>();

    useEffect(() => {
        const hub = new MessageHubBase();
        const exposed = new MessageHubBase();
        const subscription = hub.exposeAs(exposed);
        subscription.add(addHub(address, exposed));
        hub.sendMessage(new ConnectToHubRequest(uiAddress, address));
        setValue(() =>
            (hubId: string) => {
                if (!hubId || hubId === id) {
                    return hub;
                }
                return parentContextValue?.(hubId);
            })
        return () => subscription.unsubscribe();
    }, [address, id, addHub]);

    if (!value) {
        return null;
    }

    return (
        <hubContext.Provider value={value} children={children}/>
    );
}