import { useContext } from "react";
import { SmappContext } from "./SmappContext";

export function useSmappContext() {
    return useContext<SmappContext>(notebookContext as any);
}