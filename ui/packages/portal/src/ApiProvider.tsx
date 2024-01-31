import { createContext, PropsWithChildren, useContext, useMemo } from "react";
import { ProjectApi } from "./app/projectApi";
import { ProjectAccessControlApi } from "./features/project/projectSettings/accessControl/projectAccessControlApi";
import { EnvAccessControlApi } from "./features/project/envSettings/accessControl/envAccessControlApi";
import { ProjectSessionApi } from "./features/project/projectSessionApi";
import { EnvSessionApi } from "./features/project/envSessionApi";

export interface ApiContext {
    ProjectApi: typeof ProjectApi;
    ProjectAccessControlApi: typeof ProjectAccessControlApi;
    EnvAccessControlApi: typeof EnvAccessControlApi;
    ProjectSessionApi: typeof ProjectSessionApi;
    EnvSessionApi: typeof EnvSessionApi;
}

export const apiContext = createContext<ApiContext>(null);

export function ApiProvider({children}: PropsWithChildren) {
    const value = useMemo(() => {
        return {
            ProjectApi,
            ProjectAccessControlApi,
            EnvAccessControlApi,
            ProjectSessionApi,
            EnvSessionApi
        };
    }, []);

    return (
        <apiContext.Provider value={value} children={children}/>
    );
}

export function useApi() {
    return useContext(apiContext);
}