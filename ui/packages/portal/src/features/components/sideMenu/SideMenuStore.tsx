import { SideMenuState } from "./SideMenuState";
import React, { PropsWithChildren, useMemo } from "react";
import { createStoreContext } from "@open-smc/store/storeContext";

export const {useStore, useSelector, StoreProvider} = createStoreContext<SideMenuState>();

export function SideMenuStoreProvider({children}: PropsWithChildren<{}>) {
    const initialState: SideMenuState = useMemo(()=> ({}), []) ;
    return <StoreProvider initialState={initialState}>{children}</StoreProvider>;
}