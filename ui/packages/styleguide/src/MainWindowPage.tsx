import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { sampleApp } from "./sampleApp/sampleApp";

export function MainWindowPage() {
    return (
        <Sandbox root={sampleApp} log={true}/>
    );
}