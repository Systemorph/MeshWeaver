import { useLocation } from "react-router-dom";
import { MessageRouter } from "./MessageRouter";
import { LayoutHub } from "./LayoutHub";
import { ControlStarter } from "./ControlStarter";
import { SignalrTransport } from "./SignalrTransport";
import { NotificationProvider } from "./notifications/NotificationProvider";

export function AppPage() {
    const {pathname} = useLocation();

    return (
        <NotificationProvider>
            <SignalrTransport>
                <MessageRouter>
                    <LayoutHub>
                        <ControlStarter area={"app"} path={pathname}/>
                    </LayoutHub>
                </MessageRouter>
            </SignalrTransport>
        </NotificationProvider>
    );
}