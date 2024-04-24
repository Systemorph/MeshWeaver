import { useEffect } from "react";
import { Provider } from "react-redux";
import { appStore } from "@open-smc/app/src/store/appStore.ts";
import App from "@open-smc/app/src/App.tsx";
import { startSynchronization } from "./startSynchronization.ts";
import { useLocation } from "react-router-dom";

export function SamplesPage() {
    const {pathname} = useLocation();

    useEffect(() => startSynchronization(pathname), [pathname]);

    return (
        <Provider store={appStore}>
            <App/>
        </Provider>
    );
}