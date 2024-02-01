import React, { createContext, Fragment, useContext, useEffect, useMemo, useState, JSX } from "react";
import { useNotification } from "rc-notification";
import { getUseSelector } from "@open-smc/store/useSelector";
import { AppStore, makeAppStore } from "./appStore";
import { notificationConfig } from "./notificationConfig";
import { v4 } from "uuid";
import { makeToastApi, ToastApi } from "./makeToastApi";
import { useNotifyOnDisconnected } from "./useNotifyOnDisconnected";
import { useConnection, useConnectionStatus } from "./Connection";
import { ControlStarter } from "./ControlStarter";
import BlockUi from "@availity/block-ui";
import "@availity/block-ui/dist/index.css";

interface AppContext {
    readonly store: AppStore;
    readonly toastApi: ToastApi;
}

export const appContext = createContext<AppContext>(null);

export function useApp() {
    return useContext(appContext);
}

export function useAppStore() {
    const {store} = useApp();
    return store;
}

export const useAppSelector = getUseSelector(useAppStore);

interface AppProps {
    projectId: string;
    id: string;
    fallback: () => JSX.Element;
}

export function App({projectId, id, fallback}: AppProps) {
    const [notificationApi, notificationContainer] = useNotification(notificationConfig);
    const [toastApi] = useState(makeToastApi(notificationApi));
    const [store] = useState(makeAppStore);
    const [key, setKey] = useState(v4);
    const connection = useConnection();
    const {started, connectionStatus} = useConnectionStatus();

    useNotifyOnDisconnected(toastApi);

    useEffect(() => connection.onReconnected(() => setKey(v4())), [connection]);

    const value = useMemo(() => ({
        store,
        toastApi,
    }), [toastApi, store]);

    if (!started) {
        return fallback?.();
    }

    return (
        <Fragment key={key}>
            <BlockUi blocking={connectionStatus === "Disconnected"} loader={null}>
                <appContext.Provider value={value}>
                    <ControlStarter area={"app"} path={`application/${projectId}/${id}`}/>
                    {notificationContainer}
                </appContext.Provider>
            </BlockUi>
        </Fragment>
    );
}

