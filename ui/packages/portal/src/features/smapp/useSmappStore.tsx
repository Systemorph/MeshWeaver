import { useSmappContext } from "./useSmappContext";

export function useSmappStore() {
    const {store} = useSmappContext();
    return store;
}