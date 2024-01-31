import { useCallback, useState } from "react";

export function useThrowAsync() {
    const [_, setError] = useState();

    return useCallback(
        (e: unknown) => {
            setError(() => {
                throw e;
            });
        },
        [setError]
    );
}