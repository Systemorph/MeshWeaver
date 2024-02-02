import { useParams } from "react-router-dom";
import { SignalrMessageRouter } from "./SignalrMessageRouter";
import { LayoutHub } from "./LayoutHub";
import { NotificationProvider } from "./notifications/NotificationProvider";
import { ControlStarter } from "./ControlStarter";

export function AppPage() {
    const {projectId, id} = useParams();

    return (
        <NotificationProvider>
            <SignalrMessageRouter>
                <LayoutHub>
                    <ControlStarter area={"app"} path={`application/${projectId}/${id}`}/>
                </LayoutHub>
            </SignalrMessageRouter>
        </NotificationProvider>
    );
}