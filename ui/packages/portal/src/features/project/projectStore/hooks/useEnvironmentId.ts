import { useEnv } from "./useEnv";

/**
 @deprecated use useEnv() instead
 */
export function useEnvironmentId() {
    const {env} = useEnv();
    return env.id;
}