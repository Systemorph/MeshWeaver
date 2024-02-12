import { PluginOption, ViteDevServer } from "vite";
import { WebSocketClientHub } from "./WebSocketClientHub";
import { makeProxy } from "@open-smc/message-hub/src/middleware/makeProxy";
import { connect } from "@open-smc/message-hub/src/middleware/connect";
import { addToContext } from "@open-smc/message-hub/src/middleware/addToContext";
import { layoutAddress, LayoutHub } from "./LayoutHub";
import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";

export const methodName = "deliverMessage";

const uiAddress = "ui";

export function playgroundServer() {
    return {
        name: "playgroundServer",
        configureServer(server: ViteDevServer) {
            const {ws} = server;

            ws.on('connection', function (socket, request)  {
                ws.clients.forEach(client => {
                    if (client.socket === socket) {
                        const clientHub = new WebSocketClientHub(client, ws);
                        const [uiHub, uiHubProxy] = makeProxy();
                        connect(clientHub, uiHubProxy);

                        const context = new SubjectHub();

                        addToContext(context, uiHub, uiAddress);
                        addToContext(context, new LayoutHub(), layoutAddress);

                        clientHub.subscribe(console.log);
                    }
                })
            });
        },
    } as PluginOption
}