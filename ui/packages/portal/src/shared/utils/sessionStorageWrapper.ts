export namespace SessionStorageWrapper {
    export function setItem(key: string, value: unknown) {
        try {
            sessionStorage.setItem(key, JSON.stringify(value));
        } catch (error) {
            
        }
    }

    export function getItem(key: string) {
        try {
            return JSON.parse(sessionStorage.getItem(key));
        } catch (error) {
            
        }
    }
}