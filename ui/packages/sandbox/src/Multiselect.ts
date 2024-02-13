import type { MultiselectView } from "@open-smc/application/src/controls/MultiselectControl";
import {
    Category,
    CategoryItemsRequest,
    CategoryItemsResponse,
    Named,
    SelectionByCategory
} from "@open-smc/application/src/contract/application.contract";
import { CategoryItemsRequestHandler } from "./CategoryItemsRequestHandler";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Multiselect extends ControlBase implements MultiselectView {
    readonly data: SelectionByCategory;
    readonly categories: Category[]
    readonly itemsRequestHandler: CategoryItemsRequestHandler;

    constructor() {
        super("MultiselectControl");

        this.receiveMessage(CategoryItemsRequest, ({categoryName}, {id}) => {
            const properties = {requestId: id};
            const callback = (items: Named[]) => this.sendMessage(new CategoryItemsResponse(items), {properties});
            this.itemsRequestHandler?.(categoryName, callback);
        });
    }
}

export class MultiselectBuilder extends ControlBuilderBase<Multiselect> {
    constructor() {
        super(Multiselect);
    }

    withCategories(categories: Category[]) {
        this.data.categories = categories;
        return this;
    }

    withItemsRequestHandler(handler: CategoryItemsRequestHandler) {
        this.data.itemsRequestHandler = handler;
        return this;
    }
}

export const makeMultiselect = () => new MultiselectBuilder();