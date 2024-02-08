import { PluginOption, ViteDevServer, WebSocket } from "vite";
import { UiHub } from "./UiHub";
import { getOrAdd } from "@open-smc/utils/src/getOrAdd";

export const methodName = "deliverMessage";

const apps = new Map<WebSocket.WebSocket, UiHub>();

export function playgroundServer() {
    return {
        name: "playgroundServer",
        configureServer(server: ViteDevServer) {
            const {ws} = server;

            ws.on('connection', (socket, data) => {
                const app = getOrAdd(apps, socket, socket => {
                    return new UiHub();
                });

                ws.on(methodName, (messageDelivery, client) => {
                    if (client.socket === socket) {
                        app.next(messageDelivery);
                    }
                });

                app.subscribe(messageDelivery => {
                    ws.clients.forEach(client => {
                        if (client.socket === socket) {
                            client.send(methodName, messageDelivery);
                        }
                    })
                });
            });
        },
    } as PluginOption
}