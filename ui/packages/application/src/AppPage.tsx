import { useParams } from "react-router-dom";
import { MessageRouter } from "./MessageRouter";
import { LayoutHub } from "./LayoutHub";
import { NotificationProvider } from "./notifications/NotificationProvider";
import { ControlStarter } from "./ControlStarter";
import { SignalrTransport } from "./SignalrTransport";

export function AppPage() {
    const {projectId, id} = useParams();

    return (
        <NotificationProvider>
            <SignalrTransport>
                <MessageRouter>
                    <LayoutHub>
                        <ControlStarter area={"app"} path={`application/${projectId}/${id}`}/>
                    </LayoutHub>
                </MessageRouter>
            </SignalrTransport>
        </NotificationProvider>
    );
}