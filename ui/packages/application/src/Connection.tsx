import { createContext, PropsWithChildren, useContext, useEffect, useMemo, useState } from "react";
import { makeSignalrConnection, SignalrConnection } from "./makeSignalrConnection";
import { createStore, Store } from "@open-smc/store/store";

import { getAppId, setAppId } from "./appId";
import { useSelector } from "@open-smc/store/useSelector";

interface ConnectionContext {
    connection: SignalrConnection;
    store: Store<State>;
}

interface State {
    started: boolean;
    connectionStatus: ConnectionStatus;
    appId: string;
}

export type ConnectionStatus = "Connecting" | "Connected" | "Disconnected";

const context = createContext<ConnectionContext>(null);

export function useConnection() {
    const {connection} = useContext(context);
    return connection;
}

export function useConnectionStatus() {
    const {store} = useContext(context);
    const started = useSelector(store, "started");
    const appId = useSelector(store, "appId");
    const connectionStatus = useSelector(store, "connectionStatus");
    return {started, connectionStatus, appId};
}

interface ConnectionProps {
    fallback?: () => JSX.Element;
}

export function Connection({fallback, children}: PropsWithChildren & ConnectionProps) {
    const [connection] = useState(makeSignalrConnection);
    const [started, setStarted] = useState<Promise<void>>();
    const [ready, setReady] = useState(false);

    const [store] = useState(makeStore());

    useEffect(() => {
        setStarted(connection.connection.start());
    }, [connection]);

    useEffect(() => {
        const {appId} = store.getState();

        started?.then(async () => {
            const serverAppId = await connection.connection.invoke('Initialize', appId);
            store?.setState(state => {
                state.connectionStatus = "Connected"
                state.started = true;
                state.appId = serverAppId;
            });
            setReady(true);
        });
    }, [started, store]);

    useEffect(() => {
        return connection.onDisconnected(error => {
            store.setState(state => {
                state.connectionStatus = "Disconnected";
            });
        });
    }, [connection, store]);

    useEffect(() => {
        return connection.onReconnected(() => {
            store.setState(state => {
                state.connectionStatus = "Connected";
            });
        });
    }, [connection, store]);

    const value = useMemo(() => ({
        connection,
        store
    }), [connection, store]);

    if (!ready) {
        return fallback?.();
    }

    return (
        <context.Provider value={value} children={children}/>
    );
}

function makeStore() {
    const store = createStore<State>({
        started: false,
        connectionStatus: "Connecting",
        appId: getAppId()
    });

    store.subscribe("appId", (value: string) => {
        setAppId(value);
    });

    return store;
}