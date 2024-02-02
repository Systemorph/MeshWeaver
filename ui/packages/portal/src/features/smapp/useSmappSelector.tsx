import { makeUseSelector } from "@open-smc/store/src/useSelector";
import { useSmappStore } from "./useSmappStore";

export const useSmappSelector = makeUseSelector(useSmappStore);
