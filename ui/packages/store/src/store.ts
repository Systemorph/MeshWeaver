import { isEmpty, isFunction, isString, remove } from "lodash";
import { getOrAdd } from "@open-smc/utils/src/getOrAdd";
import { Draft, produce, setAutoFreeze } from "immer";
export {castDraft} from "immer";

setAutoFreeze(false);

export type Selector<TState, TValue> = (state: TState) => TValue;
export type Listener<TValue = unknown> = (value: TValue) => void;

export type Store<TState> = {
    getState: () => TState;
    setState: <TDraft = Draft<TState>>(state: Partial<TState> | ((state: TDraft) => TDraft | void)) => TState;
    subscribe: <TValue>(selector: keyof TState | Selector<TState, TValue>, listener: Listener<TValue>) => () => void;
    notify: <TValue>(selector: Selector<TState, TValue>) => void;
}

export function createStore<TState>(initialState: TState): Store<TState> {
    const listenersBySelector = new Map<Selector<TState, any>, Listener<any>[]>();
    const keySelectors = new Map<keyof TState, Selector<TState, any>>();

    function getOrAddListeners<TValue>(selector: Selector<TState, TValue>) {
        if (!listenersBySelector.has(selector)) {
            listenersBySelector.set(selector, []);
        }
        return listenersBySelector.get(selector);
    }

    let currentState = initialState;

    function getState() {
        return currentState;
    }

    function setState<TDraft = Draft<TState>>(recipe: Partial<TState> | ((draft: TDraft) => TDraft | void)) {
        // backward compatibility
        if (!isFunction(recipe)) {
            return setStateDeprecated(recipe);
        }

        const previousState = currentState;

        currentState = produce(currentState, recipe);

        for (const key in currentState) {
            if (currentState[key] === previousState[key]) {
                continue;
            }

            const selector = keySelectors.get(key);

            if (selector) {
                notify(selector);
            }
        }

        return currentState;
    }

    function setStateDeprecated(state: Partial<TState>) {
        currentState = {...currentState};

        for (const key in state) {
            if (state[key] === currentState[key]) {
                continue;
            }

            currentState[key] = state[key];

            const selector = keySelectors.get(key);

            if (selector) {
                notify(selector);
            }
        }

        return currentState;
    }

    function subscribe<TValue>(keyOrSelector: keyof TState | Selector<TState, TValue>, listener: Listener<TValue>) {
        const selector = isString(keyOrSelector)
            ? getOrAdd(keySelectors, keyOrSelector, key => state => state[key])
            : keyOrSelector as Selector<TState, TValue>;

        const listeners = getOrAddListeners(selector);

        listeners.push(listener);

        return () => {
            remove(listeners, l => l === listener);

            if (isEmpty(listeners)) {
                listenersBySelector.delete(selector);
            }
        };
    }

    function notify<TValue>(selector: Selector<TState, TValue>) {
        listenersBySelector.get(selector)?.forEach(listener => listener(selector(currentState)));
    }

    return {
        getState,
        setState,
        subscribe,
        notify
    }
}