import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { LayoutArea } from "../LayoutArea";
import { Control } from "./Control";

@contractMessage("OpenSmc.Layout.Composition")
export class LayoutStackControl extends Control {
    areas: LayoutArea[];
}