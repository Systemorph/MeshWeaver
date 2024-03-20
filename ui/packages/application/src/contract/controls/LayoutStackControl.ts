import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { LayoutArea } from "../LayoutArea";
import { Control } from "./Control";

@contractMessage("OpenSmc.Layout.Composition.LayoutStackControl")
export class LayoutStackControl extends Control {
    areas: LayoutArea[];
}