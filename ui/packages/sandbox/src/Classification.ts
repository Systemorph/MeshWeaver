import {
    Category,
    CategoryItemsRequest,
    CategoryItemsResponse,
    Named,
    SelectionByCategory
} from "@open-smc/application/application.contract";
import type { ClassificationView } from "@open-smc/application/controls/ClassificationControl";
import { CategoryItemsRequestHandler } from "./CategoryItemsRequestHandler";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Classification extends ControlBase implements ClassificationView {
    readonly selection: SelectionByCategory;
    readonly elementsCategory: Category;
    readonly classificationCategories: Category[];
    readonly itemsRequestHandler: CategoryItemsRequestHandler;

    constructor() {
        super("ClassificationControl");

        this.receiveMessage(CategoryItemsRequest, ({categoryName}, {id}) => {
            const properties = {requestId: id};
            const callback = (items: Named[]) => this.sendMessage(new CategoryItemsResponse(items), {properties});
            this.itemsRequestHandler?.(categoryName, callback);
        })
    }
}

export class ClassificationBuilder extends ControlBuilderBase<Classification> {
    constructor() {
        super(Classification);
    }

    withElementsCategory(category: Category) {
        this.data.elementsCategory = category;
        return this;
    }

    withClassificationCategories(categories: Category[]) {
        this.data.classificationCategories = categories;
        return this;
    }

    withItemsRequestHandler(handler: CategoryItemsRequestHandler) {
        this.data.itemsRequestHandler = handler;
        return this;
    }

    withSelection(value: SelectionByCategory) {
        this.data.selection = value;
        return this;
    }
}

export const makeClassification = () => new ClassificationBuilder();