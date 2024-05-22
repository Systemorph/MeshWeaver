import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { omit, pickBy } from "lodash-es";
import { isBinding } from "@open-smc/data/src/contract/Binding";

export const extractBindings = (control: UiControl) =>
    pickBy(omit(control, 'dataContext'), isBinding)