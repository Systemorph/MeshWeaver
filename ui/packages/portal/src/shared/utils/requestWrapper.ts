import axios, { AxiosError, AxiosRequestConfig } from "axios";

export async function get<T>(url: string, config?: AxiosRequestConfig) {
    try {
        const response = config ? await axios.get<T>(url, config) : await axios.get<T>(url);
        return response;
    }
    catch (error) {
        if ((error as AxiosError)?.response) {
            throw (error as AxiosError)?.response?.status;
        }
    }
}

export type Page<T> = Awaited<ReturnType<typeof getPage<T>>>;

export async function getPage<T>(url: string, config?: AxiosRequestConfig) {
    const response = await get<T[]>(url, config);

    return {
        rows: response.data,
        totalCount: parseInt(response.headers['x-total-count'])
    };
}