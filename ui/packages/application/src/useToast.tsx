import "./toast.scss";
import { useApp } from "./App";

export function useToast() {
    const {toastApi} = useApp();
    return toastApi;
}