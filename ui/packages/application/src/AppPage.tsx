import { useLocation } from "react-router-dom";
import { Provider } from "react-redux";
import { store } from "./app/store";
import React from "react";
import App from "./App";
import { registerControlResolver } from "./controlRegistry";
import { applicationControlsResolver } from "./applicationControlResolver";

registerControlResolver(applicationControlsResolver);

export function AppPage() {
    const {pathname} = useLocation();

    return (
        <Provider store={store}>
            <App/>
        </Provider>
    );
}