import { PluginOption, ViteDevServer } from "vite";
import { WebSocketClientHub } from "./WebSocketClientHub";

export const methodName = "deliverMessage";

export function playgroundServer() {
    return {
        name: "playgroundServer",
        configureServer(server: ViteDevServer) {
            const {ws} = server;

            ws.on('connection', function (socket, request)  {
                ws.clients.forEach(client => {
                    if (client.socket === socket) {
                        const clientHub = new WebSocketClientHub(client, ws);
                        clientHub.subscribe(console.log);
                    }
                })
            });
        },
    } as PluginOption
}