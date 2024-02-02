import { makeProgress } from "@open-smc/sandbox/src/Progress";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";

const progressTemplate = makeProgress()
    .withProgress(50)
    .withMessage('loading')
    .build();

export function ProgressPage() {
    return (
        <>
            <h3>Sample progress</h3>
            <Sandbox root={progressTemplate}/>
        </>
    );
}