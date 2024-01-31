import { useState, useCallback } from 'react'

export function useIncrement(initialValue = 0, step = 1): [number, () => void] {
    const [value, setValue] = useState(initialValue);
    const increment = useCallback(() => {
        setValue(x => x + step);
    }, []);
    return [value, increment];
}