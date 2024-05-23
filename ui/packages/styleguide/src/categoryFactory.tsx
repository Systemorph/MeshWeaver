import { Category, Named } from "@open-smc/application/src/contract/application.contract";
import { chance } from "./chance";

export function makeCategoryFactory() {
    const itemsCache: Record<string, Named[]> = {};

    function makeCategory(numberOfItems = 10) {
        const name = chance.company();
        itemsCache[name] = chance.n(chance.name, numberOfItems).map(asNamed);

        return {
            category: name,
            displayName: name,
            type: "Complete"
        } as Category;
    }

    function makeCategories(n = 10) {
        return chance.n(makeCategory, n);
    }

    function getItems(category: string, n?: number) {
        const items = itemsCache[category];
        return n ? items.slice(0, n) : items;
    }

    return {
        makeCategory,
        makeCategories,
        getItems
    };
}

function asNamed(name: string) {
    return {
        systemName: name,
        displayName: name
    };
}