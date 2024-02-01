import { App } from "./App";
import { useParams } from "react-router-dom";
import { Connection } from "./Connection";
import { SignalrMessageRouter } from "./SignalrMessageRouter";
import { LayoutHub } from "./LayoutHub";

const log = process.env.NODE_ENV === 'development';

export function AppPage() {
    const {projectId, id} = useParams();

    const fallback = () => <div>Loading...</div>;

    return (
        <Connection fallback={fallback}>
            <SignalrMessageRouter log={log}>
                <LayoutHub>
                    <App fallback={fallback} projectId={projectId} id={id}/>
                </LayoutHub>
            </SignalrMessageRouter>
        </Connection>
    );
}