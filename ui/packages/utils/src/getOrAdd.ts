export function getOrAdd<TKey, TValue>(map: Map<TKey, TValue>, key: TKey, factory: (key: TKey) => TValue) {
    if (!map.has(key)) {
        map.set(key, factory(key));
    }
    return map.get(key);
}