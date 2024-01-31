import { ProjectState } from "./projectState";
import { createContext, PropsWithChildren, useContext, useMemo } from "react";
import { createStore, Selector, Store } from "@open-smc/store/store";
import { getUseSelector } from "@open-smc/store/useSelector";
import { useSelectorDeprecated } from "@open-smc/store/storeContext";

interface ProjectContext {
    readonly store: Store<ProjectState>;
}

const context = createContext<ProjectContext>(null);

interface ProjectContextProviderProps {
    state: ProjectState;
}

export function ProjectContextProvider({state, children}: PropsWithChildren<ProjectContextProviderProps>) {
    const value = useMemo(() => {
        return {
            store: createStore(state)
        };
    }, [state]);

    return (
        <context.Provider value={value} children={children}/>
    );
}

export function useProjectStore() {
    const {store} = useContext(context);
    return store;
}


export const useProjectSelector = getUseSelector(useProjectStore);

/**
 @deprecated use useNotebookSelector instead (4/20/2023, akravets)
 */
export function useSelector<TValue>(selector: Selector<ProjectState, TValue>) {
    const store = useProjectStore();
    return useSelectorDeprecated(store, selector);
}