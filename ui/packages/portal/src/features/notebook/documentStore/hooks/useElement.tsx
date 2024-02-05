import { makeUseSelector } from "@open-smc/store/src/useSelector";
import { useElementsStore } from "../../NotebookEditor";

export const useElement = makeUseSelector(useElementsStore);