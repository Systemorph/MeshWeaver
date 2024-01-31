const appIdKey = "appId";

export const getAppId = () => window.sessionStorage.getItem(appIdKey);

export const setAppId = (appId: string) => window.sessionStorage.setItem(appIdKey, appId);