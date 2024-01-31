import { Sandbox } from "@open-smc/sandbox/Sandbox";
import { makeMultiselect } from "@open-smc/sandbox/Multiselect";
import { makeCategoryFactory } from "./categoryFactory";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";
import { Button } from "@open-smc/ui-kit/components/Button";
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