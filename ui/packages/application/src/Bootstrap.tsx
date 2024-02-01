import "./App.scss";
import "./SmappApp.scss";
import { Connection } from "./Connection";
import { PropsWithChildren } from "react";
import { SignalrMessageRouter } from "./SignalrMessageRouter";

export function Bootstrap({children}: PropsWithChildren) {
    return (
        <Connection>
            <SignalrMessageRouter log={true}>
                {children}
            </SignalrMessageRouter>
        </Connection>
    );
}