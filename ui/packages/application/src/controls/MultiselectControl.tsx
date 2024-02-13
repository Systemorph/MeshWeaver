import { getSelectedElements } from "./multiselect/Multiselect";
import { Category, SelectionByCategory } from "../contract/application.contract";
import { useControlContext } from "../ControlContext";
import { Multiselect } from "./multiselect/Multiselect";
import { ControlView } from "../ControlDef";

export interface MultiselectView extends ControlView {
    categories: Category[];
    selection?: SelectionByCategory;
}

export default function MultiselectControl({id, categories, selection}: MultiselectView) {
    const {onChange} = useControlContext();

    return (
        <div id={id}>
            <Multiselect
                data={selection}
                categories={categories}
                onChange={data => onChange("data", getSelectedElements(data))}
            />
        </div>
    );
}