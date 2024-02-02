import { createContext, PropsWithChildren, useContext, useEffect, useState } from "react";
import { makeSignalrConnection, SignalrConnection } from "./makeSignalrConnection";
import { createStore, Store } from "@open-smc/store/src/store";
import { getAppId, setAppId } from "./appId";
import { makeUseSelector } from "@open-smc/store/src/useSelector";
import { useToast } from "./notifications/useToast";
import { v4 } from "uuid";
import BlockUi from "@availity/block-ui";
import "@availity/block-ui/dist/index.css";

interface State {
    connection: SignalrConnection;
    started: boolean;
    reconnectionCount: number;
    connectionStatus: ConnectionStatus;
    appId: string;
}

export type ConnectionStatus = "Connecting" | "Connected" | "Disconnected";

const context = createContext<Store<State>>(null);

function useConnectionStore() {
    return useContext(context);
}

export const useConnectionSelector = makeUseSelector(useConnectionStore);

const DISCONNECTED_KEY = 'Disconnected';

export function SignalrConnectionProvider({children}: PropsWithChildren) {
    const [signalrConnection] = useState(makeSignalrConnection);
    const {connection, onDisconnected, onReconnected} = signalrConnection;
    const [startPromise, setStartPromise] = useState<Promise<void>>();
    const [store] = useState(makeStore(signalrConnection));
    const {showToast, closeToast} = useToast();
    const [key, setKey] = useState(v4);

    useEffect(() => onReconnected(() => setKey(v4())), [onReconnected]);

    async function initialize() {
        const {appId} = store.getState();
        const serverAppId = await connection.invoke('Initialize', appId);
        store.setState(state => {
            state.connectionStatus = "Connected"
            state.started = true;
            state.appId = serverAppId;
        });
    }

    useEffect(() => {
        setStartPromise(connection.start());
    }, [connection]);

    useEffect(() => {
        return onDisconnected(() => {
            store.setState(state => {
                state.connectionStatus = "Disconnected";
            });

            showToast(
                'Disconnected',
                'Trying to reconnect...',
                'Error',
                {
                    closable: false,
                    duration: null,
                    key: DISCONNECTED_KEY
                }
            );
        });
    }, [onDisconnected, store, showToast]);

    useEffect(() => {
        return onReconnected(async () => {
            await initialize();

            closeToast(DISCONNECTED_KEY);
            showToast('Reconnected', 'Connection restored successfully.', 'Success');
        });
    }, [onReconnected, store, closeToast, showToast]);

    useEffect(() => {
        if (startPromise && store) {
            startPromise.then(initialize);
        }
    }, [startPromise, store]);

    const {connectionStatus} = store.getState();

    if (connectionStatus === "Connecting") {
        return <div>Loading...</div>;
    }

    return (
        <BlockUi blocking={connectionStatus === "Disconnected"} loader={null}>
            <context.Provider value={store} children={children} key={key}/>
        </BlockUi>
    );
}

function makeStore(connection: SignalrConnection) {
    const store = createStore<State>({
        connection,
        started: false,
        reconnectionCount: 0,
        connectionStatus: "Connecting",
        appId: getAppId()
    });

    store.subscribe("appId", (value: string) => {
        setAppId(value);
    });

    return store;
}