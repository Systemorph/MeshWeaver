import { formatNumber } from "./numbers";

export const withTimeout = <T>(promise: Promise<T>, delay: number) => {
    let removeTimeout: () => void;

    const timeoutPromise = new Promise<T>((_, reject) => {
        const timeoutId = setTimeout(() =>
            reject(`Timed out after ${formatNumber(delay / 1000, '0.#') + ' seconds'}.`), delay);
        removeTimeout = () => {
            clearTimeout(timeoutId);
        }
    });

    promise.finally(removeTimeout);

    return Promise.race([
        promise,
        timeoutPromise
    ]);
};