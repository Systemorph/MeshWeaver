import { distinctUntilChanged, map} from "rxjs";
import { expandBindings } from "./expandBindings";
import { cloneDeepWith, isEqual } from "lodash-es";
import { setArea } from "./appReducer";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { collections } from "./entityStore";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { appStore } from "./appStore";
import { selectDeep } from "@open-smc/data/src/operators/selectDeep";

export const dataBinding = (id: string, parentDataContext: unknown) =>
    (previous: UiControl, control: UiControl) => {
        if (control) {
            const componentTypeName = control.constructor.name;
            const {dataContext, ...props} = control;

            return collections
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

export const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof EntityReference
            ? value.id : undefined
    );