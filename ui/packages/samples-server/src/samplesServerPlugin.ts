import { PluginOption, ViteDevServer } from "vite";
import { WebSocketClientHub } from "./WebSocketClientHub";
import { SamplesServer } from "./SamplesServer";
import { SerializationMiddleware } from "@open-smc/middleware/src/SerializationMiddleware";
import "@open-smc/data/src/contract";
import { connectHubs } from "@open-smc/messaging/src/middleware/connectHubs.ts";

export function samplesServerPlugin() {
    return {
        name: "samplesServer",
        configureServer(server: ViteDevServer) {
            const {ws} = server;

            ws.on('connection', function (socket, request)  {
                ws.clients.forEach(client => {
                    if (client.socket === socket) {
                        const clientHub = new WebSocketClientHub(client, ws);
                        connectHubs(new SerializationMiddleware(clientHub), new SamplesServer());
                        // const [uiHub, uiHubProxy] = makeProxy();
                        // connect(clientHub, uiHubProxy);
                        //
                        // const context = new Subject<MessageDelivery>();
                        //
                        // addToContext(context, uiHub, uiAddress);
                        // addToContext(context, new SampleApp(), layoutAddress);
                    }
                })
            });
        },
    } as PluginOption
}