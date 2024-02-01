import { useParams } from "react-router-dom";
import { SignalrConnectionProvider } from "./SignalrConnectionProvider";
import { SignalrMessageRouter } from "./SignalrMessageRouter";
import { LayoutHub } from "./LayoutHub";
import { NotificationProvider } from "./notifications/NotificationProvider";
import { ControlStarter } from "./ControlStarter";

const log = process.env.NODE_ENV === 'development';

export function AppPage() {
    const {projectId, id} = useParams();
    const fallback = () => <div>Loading...</div>;

    return (
        <NotificationProvider>
            <SignalrConnectionProvider fallback={fallback}>
                <SignalrMessageRouter fallback={fallback} log={log}>
                    <LayoutHub>
                        <ControlStarter area={"app"} path={`application/${projectId}/${id}`}/>
                    </LayoutHub>
                </SignalrMessageRouter>
            </SignalrConnectionProvider>
        </NotificationProvider>
    );
}