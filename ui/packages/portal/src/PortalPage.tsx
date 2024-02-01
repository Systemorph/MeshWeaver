import { Connection } from "@open-smc/application/Connection";
import { SignalrMessageRouter } from "@open-smc/application/SignalrMessageRouter";
import { Outlet } from "react-router-dom";
import { ApiProvider } from "./ApiProvider";
import { LayoutHub } from "@open-smc/application/LayoutHub";

const log = process.env.NODE_ENV === 'development';

export function PortalPage() {
    const fallback = () => <div>Loading...</div>;

    return (
        <ApiProvider>
            <Connection fallback={fallback}>
                <SignalrMessageRouter log={log}>
                    <LayoutHub>
                        <Outlet/>
                    </LayoutHub>
                </SignalrMessageRouter>
            </Connection>
        </ApiProvider>
    );
}

