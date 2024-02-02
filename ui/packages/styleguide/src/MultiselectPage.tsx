import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { makeMultiselect } from "@open-smc/sandbox/src/Multiselect";
import { makeCategoryFactory } from "./categoryFactory";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { useState } from "react";

export function MultiselectPage() {
    const [multiselect, setMultiselect] = useState(makeMultiselectSample);

    return (
        <div>
            <Button label={"Reset"} onClick={() => setMultiselect(makeMultiselectSample)}/>
            <Sandbox root={multiselect} log={true}/>
        </div>
    );
}

function makeMultiselectSample() {
    const {makeCategories, getItems} = makeCategoryFactory();

    return makeMultiselect()
        .withCategories(makeCategories(10))
        .withItemsRequestHandler((category, callback) => callback(getItems(category)))
        .withDataContext({
            $scopeId: "my-scope",
            data: {}
        })
        .withData(makeBinding("data"))
        .build();
}