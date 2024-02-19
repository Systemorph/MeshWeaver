import { ControlStarter } from "@open-smc/application/src/ControlStarter";
import { ViteHmrTransport } from './ViteHmrTransport';
import { LayoutHub } from "@open-smc/application/src/LayoutHub";
import { MessageRouter } from "@open-smc/application/src/MessageRouter";
import { useLocation } from "react-router-dom";

export function PlaygroundPage() {
    const {pathname} = useLocation();

    return (
        <ViteHmrTransport>
            <MessageRouter>
                <LayoutHub>
                    <ControlStarter area={"app"} path={pathname}/>
                </LayoutHub>
            </MessageRouter>
        </ViteHmrTransport>
    );
}