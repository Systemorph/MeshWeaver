import {
    Classification, getSelectedElements,
} from "./classification/Classification";
import { useControlContext } from "../ControlContext";
import { Category, SelectionByCategory } from "@open-smc/layout/src/contract/application.contract";
import { ControlView } from "../ControlDef";

export interface ClassificationView extends ControlView {
    elementsCategory: Category;
    classificationCategories: Category[];
    selection?: SelectionByCategory;
}

export default function ClassificationControl({id, elementsCategory, classificationCategories, selection}: ClassificationView) {
    const {onChange} = useControlContext();

    return (
        <div id={id}>
            <Classification
                data={selection}
                elementsCategory={elementsCategory}
                classificationCategories={classificationCategories}
                onChange={data => onChange("selection", getSelectedElements(data))}
            />
        </div>
    );
}