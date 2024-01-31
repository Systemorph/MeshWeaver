import "./App.scss";
import "./SmappApp.scss";
import { Connection } from "./Connection";
import { PropsWithChildren } from "react";
import { MessageRouter } from "./MessageRouter";

export function Bootstrap({children}: PropsWithChildren) {
    return (
        <Connection>
            <MessageRouter log={true}>
                {children}
            </MessageRouter>
        </Connection>
    );
}