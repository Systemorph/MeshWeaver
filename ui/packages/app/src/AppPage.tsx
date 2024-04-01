import { useLocation } from "react-router-dom";
import { Provider } from "react-redux";
import React from "react";
import App from "./App";
import { registerControlResolver } from "./controlRegistry";
import { applicationControlsResolver } from "./applicationControlResolver";
import { appStore } from "./store/appStore";

registerControlResolver(applicationControlsResolver);

export function AppPage() {
    const {pathname} = useLocation();

    return (
        <Provider store={appStore}>
            <App/>
        </Provider>
    );
}