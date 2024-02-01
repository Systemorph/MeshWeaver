import { Sandbox } from "@open-smc/sandbox/Sandbox";
import { sampleApp } from "./sampleApp/sampleApp";

export function MainWindowPage() {
    return (
        <Sandbox root={sampleApp} log={true}/>
    );
}