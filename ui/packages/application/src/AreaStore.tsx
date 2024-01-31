import { createStore, Store } from "@open-smc/store/store";
import { AreaChangedEvent } from "./application.contract";
import { createContext, PropsWithChildren, useContext, useEffect, useMemo } from "react";
import { useMessageHub } from "./messageHub/AddHub";
import { getUseSelector } from "@open-smc/store/useSelector";

interface AreaStoreContext {
    areaStore: Store<Record<string, AreaChangedEvent>>;
}

const context = createContext<AreaStoreContext>(null);

export function useAreaStore() {
    const {areaStore} = useContext(context);
    return areaStore;
}

export const useAreaSelector = getUseSelector(useAreaStore);

export function AreaStore({children}: PropsWithChildren) {
    const value = useMemo(() => {
        const areaStore = createStore<Record<string, AreaChangedEvent>>({});

        return {
            areaStore
        };
    }, []);

    const messageHub = useMessageHub();
    const {areaStore} = value;

    useEffect(() => {
        return messageHub.receiveMessage(AreaChangedEvent,
            (message) => {
                areaStore.setState(areas => {
                    areas[message.area] = message;
                });
            });
    }, [messageHub, areaStore]);

    return (
        <context.Provider value={value} children={children}/>
    );
}