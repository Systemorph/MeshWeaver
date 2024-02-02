import { SignalrTransport } from "@open-smc/application/src/SignalrTransport";
import { MessageRouter } from "@open-smc/application/src/MessageRouter";
import { Outlet } from "react-router-dom";
import { ApiProvider } from "./ApiProvider";
import { LayoutHub } from "@open-smc/application/src/LayoutHub";

export function PortalPage() {
    return (
        <ApiProvider>
            <SignalrTransport>
                <MessageRouter>
                    <LayoutHub>
                        <Outlet/>
                    </LayoutHub>
                </MessageRouter>
            </SignalrTransport>
        </ApiProvider>
    );
}

