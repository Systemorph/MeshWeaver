import { PluginOption, ViteDevServer } from "vite";
import { WebSocketClientHub } from "./WebSocketClientHub";
import { makeProxy } from "@open-smc/message-hub/src/middleware/makeProxy";
import { connect } from "@open-smc/message-hub/src/middleware/connect";
import { addToContext } from "@open-smc/message-hub/src/middleware/addToContext";
import { LayoutHub } from "./LayoutHub";

import { layoutAddress, uiAddress } from "./contract.ts";
import { Subject } from "rxjs";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery.ts";

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

                        const context = new Subject<MessageDelivery>();

                        addToContext(context, uiHub, uiAddress);
                        addToContext(context, new LayoutHub(), layoutAddress);
                    }
                })
            });
        },
    } as PluginOption
}