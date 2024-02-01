import { SignalrConnectionProvider } from "@open-smc/application/SignalrConnectionProvider";
import { SignalrMessageRouter } from "@open-smc/application/SignalrMessageRouter";
import { Outlet } from "react-router-dom";
import { ApiProvider } from "./ApiProvider";
import { LayoutHub } from "@open-smc/application/LayoutHub";

const log = process.env.NODE_ENV === 'development';

export function PortalPage() {
    const fallback = () => <div>Loading...</div>;

    return (
        <ApiProvider>
            <SignalrConnectionProvider fallback={fallback}>
                <SignalrMessageRouter log={log}>
                    <LayoutHub>
                        <Outlet/>
                    </LayoutHub>
                </SignalrMessageRouter>
            </SignalrConnectionProvider>
        </ApiProvider>
    );
}

