import { Control } from "./Control";
import { type } from "@open-smc/serialization/src/type";
import { LayoutArea } from "../LayoutArea";

@type("OpenSmc.Layout.Composition.LayoutStackControl")
export class LayoutStackControl extends Control {
    areas: LayoutArea[];
}