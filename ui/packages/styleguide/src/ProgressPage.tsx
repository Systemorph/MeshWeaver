import { makeProgress } from "@open-smc/sandbox/Progress";
import { Sandbox } from "@open-smc/sandbox/Sandbox";

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