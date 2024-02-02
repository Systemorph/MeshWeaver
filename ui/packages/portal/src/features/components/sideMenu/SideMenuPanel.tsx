import { createContext, PropsWithChildren, useContext, useEffect, useMemo } from "react";
import { createStore, Store } from "@open-smc/store/store";
import { makeUseSelector } from "@open-smc/store/useSelector";

interface SideMenuPanelProps {
    isOpen: boolean;
}

export function SideMenuPanel({isOpen, children}: PropsWithChildren<SideMenuPanelProps>) {
    const contextValue = useMemo(() => {
        const store = createStore({
            isOpen
        });

        return {
            store,
        };
    }, []);

    const {store} = contextValue;

    useEffect(() => {
        contextValue.store.setState(state => {
            state.isOpen = isOpen;
        });
    }, [isOpen]);

    return (
        <context.Provider value={contextValue} children={children}/>
    );
}

interface SideMenuPanelContext {
    store: Store<SideMenuPanelState>;
}

interface SideMenuPanelState {
    isOpen: boolean;
}

const context = createContext<SideMenuPanelContext>(null);

export function useSideMenuPanelContext() {
    return useContext(context);
}

export function useSideMenuPanelStore() {
    const {store} = useSideMenuPanelContext();
    return store;
}

export const useSideMenuPanelSelector = makeUseSelector(useSideMenuPanelStore);