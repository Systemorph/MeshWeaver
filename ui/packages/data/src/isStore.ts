import { Store } from "@reduxjs/toolkit";
import { isFunction } from "lodash-es";

export const isStore = (value: any): value is Store => isFunction(value?.dispatch);