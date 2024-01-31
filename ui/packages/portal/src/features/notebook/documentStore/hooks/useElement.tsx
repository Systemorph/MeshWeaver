import { getUseSelector } from "@open-smc/store/useSelector";
import { useElementsStore } from "../../NotebookEditor";

export const useElement = getUseSelector(useElementsStore);