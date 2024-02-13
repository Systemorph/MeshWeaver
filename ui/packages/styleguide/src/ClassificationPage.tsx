import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { makeCategoryFactory } from "./categoryFactory";
import { SelectionByCategory } from "@open-smc/application/src/contract/application.contract";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { makeClassification } from "@open-smc/sandbox/src/Classification";
import { makeStack } from "@open-smc/sandbox/src/LayoutStack";

const {makeCategory, makeCategories, getItems} = makeCategoryFactory();

const elementsCategory = makeCategory(15);
const classificationCategories = makeCategories(3);
const data = {
    [classificationCategories[0].category]: getItems(elementsCategory.category).slice(0, 3)
} as SelectionByCategory;

const classification = makeStack()
    .withDataContext({
        $scopeId: "my-scope",
        data
    })
    .withView(
        makeClassification()
            .withElementsCategory(elementsCategory)
            .withClassificationCategories(classificationCategories)
            .withItemsRequestHandler((category, callback) => callback(getItems(category)))
            .withSelection(makeBinding("data"))
    )
    .build();

export function ClassificationPage() {
    return (
        <div>
            <div>
                <Sandbox root={classification} log={true}/>
            </div>
        </div>
    );
}