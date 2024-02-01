import { Connection } from "@open-smc/application/Connection";
import { MessageRouter } from "@open-smc/application/MessageRouter";
import { Outlet } from "react-router-dom";
import { ApiProvider } from "./ApiProvider";
import { LayoutHub } from "@open-smc/application/LayoutHub";

const log = process.env.NODE_ENV === 'development';

export function PortalPage() {
    const fallback = () => <div>Loading...</div>;

    return (
        <ApiProvider>
            <Connection fallback={fallback}>
                <MessageRouter log={log}>
                    <LayoutHub>
                        <Outlet/>
                    </LayoutHub>
                </MessageRouter>
            </Connection>
        </ApiProvider>
    );
}

