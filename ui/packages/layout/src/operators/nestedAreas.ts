import { map, Observable } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { LayoutStackControl } from "../contract/controls/LayoutStackControl";
import { Control } from "../contract/controls/Control";

export const nestedAreas = () =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(
                map(
                    layoutArea => layoutArea?.control &&
                        nestedAreaProjections(layoutArea.control)
                            ?.map(project => project(source))
                )
            );

const nestedAreaProjections = (control: Control) => {
    if (control instanceof LayoutStackControl) {
        return control?.areas.map(
            (area, index) =>
                (source: Observable<LayoutArea>) =>
                    source.pipe(
                        map(
                            layoutArea =>
                                (layoutArea.control as LayoutStackControl).areas[index]
                        )
                    )
        );
    }
}