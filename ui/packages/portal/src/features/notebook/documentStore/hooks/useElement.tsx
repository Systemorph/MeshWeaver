import { makeUseSelector } from "@open-smc/store/useSelector";
import { useElementsStore } from "../../NotebookEditor";

export const useElement = makeUseSelector(useElementsStore);