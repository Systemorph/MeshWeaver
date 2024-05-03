import { useLocation } from "react-router-dom";
import { Provider } from "react-redux";
import React, { useEffect, useState } from "react";
import App from "./App";
import { registerControlResolver } from "./controlRegistry";
import { applicationControlsResolver } from "./applicationControlResolver";
import { appStore } from "./store/appStore";
import { SignalrHub } from "./signalr/SignalrHub";
import { makeSignalrConnection } from "./signalr/makeSignalrConnection";
import { renderLayoutAreaReference } from "./store/renderLayoutAreaReference";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { connectHubs } from "@open-smc/messaging/src/middleware/connectHubs";
import { UiHub } from "./UiHub";
import { SerializationMiddleware } from "@open-smc/middleware/src/SerializationMiddleware";

registerControlResolver(applicationControlsResolver);

export function AppPage() {
    const {pathname} = useLocation();
    const [signalrHub] = useState(new SignalrHub(makeSignalrConnection()));
    const [uiHub] = useState(new UiHub());

    useEffect(() => {
       const subscription = connectHubs(signalrHub, uiHub);
       return () => subscription.unsubscribe();
    }, [new SerializationMiddleware(signalrHub), uiHub]);

    useEffect(
        () => renderLayoutAreaReference(uiHub, new LayoutAreaReference(pathname)),
        [signalrHub, uiHub, pathname]
    );

    return (
        <Provider store={appStore}>
            <App/>
        </Provider>
    );
}