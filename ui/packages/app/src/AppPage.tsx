import { useLocation } from "react-router-dom";
import { Provider } from "react-redux";
import React, { useEffect, useState } from "react";
import App from "./App";
import { registerControlResolver } from "./controlRegistry";
import { applicationControlsResolver } from "./applicationControlResolver";
import { appStore } from "./store/appStore";
import { startSynchronization } from "./store/startSynchronization";
import { SignalrHub } from "./signalr/SignalrHub";
import { makeSignalrConnection } from "./signalr/makeSignalrConnection";

registerControlResolver(applicationControlsResolver);

export function AppPage() {
    const {pathname} = useLocation();

    const [signalrHub] = useState(new SignalrHub(makeSignalrConnection()));

    useEffect(() => startSynchronization(signalrHub), []);

    return (
        <Provider store={appStore}>
            <App/>
        </Provider>
    );
}