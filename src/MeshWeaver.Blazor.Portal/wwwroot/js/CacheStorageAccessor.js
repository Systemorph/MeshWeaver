// CacheStorageAccessor.js
// Provides JavaScript functions for Cache Storage API access

const CACHE_NAME = 'meshweaver-cache';

// Helper to create a cache key from request details
function getCacheKey(url, method, requestBody) {
    const key = `${method}:${url}`;
    if (requestBody) {
        return `${key}:${requestBody}`;
    }
    return key;
}

// Store a request/response pair in cache
export async function put(url, method, requestBody, responseBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        const response = new Response(responseBody, {
            headers: { 'Content-Type': 'application/json' }
        });
        await cache.put(cacheKey, response);
        console.log(`Cached: ${cacheKey}`);
    } catch (error) {
        console.error('Error caching data:', error);
    }
}

// Retrieve a cached response
export async function get(url, method, requestBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        const cachedResponse = await cache.match(cacheKey);

        if (cachedResponse) {
            const responseBody = await cachedResponse.text();
            console.log(`Cache hit: ${cacheKey}`);
            return responseBody;
        }

        console.log(`Cache miss: ${cacheKey}`);
        return null;
    } catch (error) {
        console.error('Error retrieving from cache:', error);
        return null;
    }
}

// Remove a specific cached item
export async function remove(url, method, requestBody) {
    try {
        const cache = await caches.open(CACHE_NAME);
        const cacheKey = getCacheKey(url, method, requestBody);
        const deleted = await cache.delete(cacheKey);
        if (deleted) {
            console.log(`Removed from cache: ${cacheKey}`);
        }
    } catch (error) {
        console.error('Error removing from cache:', error);
    }
}

// Remove all cached items
export async function removeAll() {
    try {
        const deleted = await caches.delete(CACHE_NAME);
        if (deleted) {
            console.log(`Cleared cache: ${CACHE_NAME}`);
        }
    } catch (error) {
        console.error('Error clearing cache:', error);
    }
}
