import { makeSpinner } from "@open-smc/sandbox/Spinner";
import { Sandbox } from "@open-smc/sandbox/Sandbox";

const spinnerTemplate = makeSpinner().build();

export function SpinnerPage() {
    return (
        <>
            <h3>Sample spinner</h3>
            <Sandbox root={spinnerTemplate}/>
        </>
    );
}