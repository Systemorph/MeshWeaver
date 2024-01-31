import React, { Context, createContext, PropsWithChildren, useContext, useEffect, useMemo } from "react";
import { createStore, Selector, Store } from "./store";
import { useCounter } from "usehooks-ts";

export type StoreProviderProps<TState> = {
    initialState: TState;
}

/**
 @deprecated use getStoreProvider, and getStoreHooks
 */
export function createStoreContext<TState>() {
    const context = createContext<Store<TState>>(null);

    const {useStore, useSelector} = getStoreHooks(context);
    const StoreProvider = getStoreProvider(context);

    return {
        useStore,
        useSelector,
        StoreProvider
    }
}

export function getStoreProvider<TState>(context: Context<Store<TState>>) {
    return function StoreProvider({initialState, children}: PropsWithChildren<StoreProviderProps<TState>>) {
        const store = useMemo(() => createStore(initialState), [initialState]);
        return <context.Provider value={store}>{children}</context.Provider>
    }
}

/**
 @deprecated  (7/13/2023, akravets)
 */
export function getStoreHooks<TState>(context: Context<Store<TState>>) {
    function useStore() {
        return useContext(context);
    }

    function useSelector<TValue>(selector: Selector<TState, TValue>) {
        const store = useStore();
        return useSelectorDeprecated(store, selector);
    }

    return {
        useStore,
        useSelector
    };
}

/**
 @deprecated use useSelectorFactory instead (4/20/2023, akravets)
 */
export function useSelectorDeprecated<TState, TValue>(store: Store<TState>, selector: Selector<TState, TValue>) {
    const {increment} = useCounter();

    useEffect(() => {
        if (store) {
            const unsubscribe = store.subscribe(selector, increment);
            return () => void unsubscribe();
        }
    }, [store, selector]);

    return selector(store.getState());
}