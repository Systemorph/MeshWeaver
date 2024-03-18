import { useAppSelector } from "@open-smc/application/src/app/hooks";
import { renderControl } from "@open-smc/application/src/renderControl";
import { NotificationProvider } from "@open-smc/application/src/notifications/NotificationProvider";

export default function App() {
    const rootArea = useAppSelector(state => state);

    if (!rootArea?.control) {
        return null;
    }

    return (
        <div className="App">
            <NotificationProvider>
                {renderControl(rootArea.control)}
            </NotificationProvider>
        </div>
    );
}