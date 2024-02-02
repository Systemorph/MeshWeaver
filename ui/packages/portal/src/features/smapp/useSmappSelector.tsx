import { makeUseSelector } from "@open-smc/store/useSelector";
import { useSmappStore } from "./useSmappStore";

export const useSmappSelector = makeUseSelector(useSmappStore);
