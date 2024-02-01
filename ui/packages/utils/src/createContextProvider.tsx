import React, { Context, PropsWithChildren, useMemo } from "react";
import { values } from "lodash";

export function createContextProvider<TContext, TProps = void>(context: Context<TContext>, factory?: (props?: TProps) => TContext) {
    return function ({children, ...props}: PropsWithChildren<TProps extends void ? TContext : TProps>) {
        const value = useMemo<TContext>(() => factory ? factory(props as TProps) : props as TContext, values(props));

        return (
            <context.Provider value={value}>
                {children}
            </context.Provider>
        );
    };
}