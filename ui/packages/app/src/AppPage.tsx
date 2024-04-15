import { useLocation } from "react-router-dom";
import { Provider } from "react-redux";
import React, { useEffect } from "react";
import App from "./App";
import { registerControlResolver } from "./controlRegistry";
import { applicationControlsResolver } from "./applicationControlResolver";
import { appStore } from "./store/appStore";
import { startSynchronization } from "./store/startSynchronization";

registerControlResolver(applicationControlsResolver);

export function AppPage() {
    const {pathname} = useLocation();

    useEffect(() => startSynchronization(), []);

    return (
        <Provider store={appStore}>
            <App/>
        </Provider>
    );
}