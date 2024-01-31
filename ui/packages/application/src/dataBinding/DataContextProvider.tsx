import { createContext, PropsWithChildren, useContext } from "react";
import { DataContext } from "./DataContext";

const context = createContext<DataContext>(null);

interface DataContextProps {
    dataContext: DataContext;
}

export function DataContextProvider({dataContext, children}: PropsWithChildren<DataContextProps>) {
    return (
        <context.Provider value={dataContext} children={children}/>
    )
}

export function useDataContext() {
    return useContext(context);
}