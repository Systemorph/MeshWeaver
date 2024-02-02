import { NotificationProvider } from "@open-smc/application/src/notifications/NotificationProvider";
import { ControlStarter } from "@open-smc/application/src/ControlStarter";
import { ViteHmrTransport } from './ViteHmrTransport';
import './App.css'
import { LayoutHub } from "@open-smc/application/src/LayoutHub";
import { MessageRouter } from "@open-smc/application/src/MessageRouter.tsx";

export function PlaygroundPage() {
    return (
        <NotificationProvider>
            <ViteHmrTransport>
                <MessageRouter>
                    <LayoutHub>
                        <ControlStarter area={"app"}/>
                    </LayoutHub>
                </MessageRouter>
            </ViteHmrTransport>
        </NotificationProvider>
    )
}