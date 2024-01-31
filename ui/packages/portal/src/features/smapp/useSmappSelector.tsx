import { getUseSelector } from "@open-smc/store/useSelector";
import { useSmappStore } from "./useSmappStore";

export const useSmappSelector = getUseSelector(useSmappStore);
