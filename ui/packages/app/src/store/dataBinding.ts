import { distinctUntilChanged, map} from "rxjs";
import { expandBindings } from "./expandBindings";
import { cloneDeepWith, isEqual } from "lodash-es";
import { selectDeep } from "@open-smc/data/src/selectDeep";
import { removeArea, setArea } from "./appReducer";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { instances$ } from "./entityStore";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { appStore } from "./appStore";

export const dataBinding = (id: string, parentDataContext: unknown) =>
    (control: UiControl) => {
        if (control) {
            const componentTypeName = control.constructor.name;
            const {dataContext, ...props} = control;

            return instances$
                .pipe(map(selectDeep(dataContext)))
                .pipe(distinctUntilChanged(isEqual))
                .pipe(map(expandBindings(nestedAreasToIds(props), parentDataContext)))
                .subscribe(
                    props => {
                        appStore.dispatch(
                            setArea({
                                id,
                                control: {
                                    componentTypeName,
                                    props
                                }
                            })
                        );
                    }
                );
        }

        appStore.dispatch(
            setArea({
                id
            })
        );
    }

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof EntityReference
            ? value.id : undefined
    );