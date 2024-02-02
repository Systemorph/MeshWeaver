import { Store } from "./store";
import { useCounter } from "usehooks-ts";
import { useEffect } from "react";

export function useSelector<TState, TKey extends keyof TState>(store: Store<TState>, key: TKey) {
    const {getState} = store;
    const {increment} = useCounter();

    useEffect(() => {
        if (store) {
            const unsubscribe = store.subscribe(key, increment);
            return () => void unsubscribe();
        }
    }, [store, key]);

    return getState()[key];
}

export const makeUseSelector = <TState extends any>(useStore: () => Store<TState>) =>
    <TKey extends keyof TState>(key: TKey) => useSelector(useStore(), key);