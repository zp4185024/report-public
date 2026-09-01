import { service, cancelRequest } from '/@/utils/request';
import {AxiosRequestConfig, AxiosResponse} from "axios";

// 接口基类
/**
 * 构造并发送 index 的 use Base Api 网络请求，随后按接口类型返回响应结果。
 * @param module - module 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const useBaseApi = (module: string) => {
    const baseUrl = `/api/${module}/`;
    /**
     * 封装 index 的网络请求入口，合并调用参数与基础配置后返回服务端响应。
     * @param config - 控制本次处理行为的配置选项。
     * @param cancel - 用于取消当前异步操作的令牌。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const request = <T>(config: AxiosRequestConfig<T>, cancel: boolean = false) => {
        // 第一阶段：检查 useBaseApi 当前条件，并按匹配分支执行对应处理。
        if (cancel) {
            cancelRequest(config.url || "");
            return Promise.resolve({} as AxiosResponse<any, any>);
        }
        return service(config);
    }
    // 第一阶段：遍历 useBaseApi 当前数据集合，逐项转换并维护处理结果。
    return {
        baseUrl: baseUrl,
        request: request,
        /**
         * 构造并发送 index 的 page 网络请求，随后按接口类型返回响应结果。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        page: function (data: any, cancel: boolean = false) {
            return request({
                url: baseUrl + "page",
                method: 'post',
                data,
            }, cancel);
        },
        /**
         * 构造并发送 index 的 detail 网络请求，随后按接口类型返回响应结果。
         * @param id - 需要定位的业务记录标识。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        detail: function (id: any, cancel: boolean = false) {
            // 第2阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                url: baseUrl + "detail",
                method: 'get',
                data: { id },
            }, cancel);
        },
        /**
         * 构造并发送 index 的 dropdown Data 网络请求，随后按接口类型返回响应结果。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        dropdownData: function (data: any, cancel: boolean = false) {
            return request({
                url: baseUrl + "dropdownData",
                method: 'post',
                data,
            }, cancel);
        },
        /**
         * 构建 index 所需的 add 内容，并保证生成结果符合调用约定。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        add: function (data: any, cancel: boolean = false) {
            return request({
                url: baseUrl + 'add',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 更新 index 的 update 状态，并同步相关业务数据。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        update: function (data: any, cancel: boolean = false) {
            // 第3阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                url: baseUrl + 'update',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 更新 index 的 set Status 状态，并同步相关业务数据。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        setStatus: function (data: any, cancel: boolean = false) {
            // 第2阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                url: baseUrl + 'setStatus',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 移除 index 中与 delete 对应的数据，并清理关联状态。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        delete: function (data: any, cancel: boolean = false) {
            // 第4阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                url: baseUrl + 'delete',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 构造并发送 index 的 batch Delete 网络请求，随后按接口类型返回响应结果。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        batchDelete: function (data: any, cancel: boolean = false) {
            return request({
                url: baseUrl + 'batchDelete',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 整理 index 的 export Data 数据，并生成可供调用方使用的输出。
         * @param data - 本次操作使用的输入数据。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        exportData: function (data: any, cancel: boolean = false) {
            return request({
                responseType: 'arraybuffer',
                url: baseUrl + 'export',
                method: 'post',
                data
            }, cancel);
        },
        /**
         * 整理 index 的 download Template 数据，并生成可供调用方使用的输出。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        downloadTemplate: function (cancel: boolean = false) {
            // 第5阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                responseType: 'arraybuffer',
                url: baseUrl + 'import',
                method: 'get',
            }, cancel);
        },
        /**
         * 接收并校验 index 的 import Data 输入，随后写入目标状态。
         * @param file - file 对应的业务输入；具体类型和空值范围以方法签名为准。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        importData: function (file: any, cancel: boolean = false) {
            const formData = new FormData();
	        formData.append('file', file);
            // 第3阶段：整理 useBaseApi 已计算的数据，并按方法契约返回最终结果。
            return request({
                headers: { 'Content-Type': 'multipart/form-data;charset=UTF-8' },
                responseType: 'arraybuffer',
                url: baseUrl + 'import',
                method: 'post',
                data: formData,
            }, cancel);
        },
        /**
         * 接收并校验 index 的 upload File 输入，随后写入目标状态。
         * @param params - 本次操作使用的输入数据。
         * @param action - action 对应的业务输入；具体类型和空值范围以方法签名为准。
         * @param cancel - 用于取消当前异步操作的令牌。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        uploadFile: function (params: any, action: string, cancel: boolean = false) {
            // 第6阶段：构造 useBaseApi 当前阶段需要的中间数据和执行参数。
            const formData = new FormData();
            formData.append('file', params.file);
            // 自定义参数
            if (params.data) {
                Object.keys(params.data).forEach((key) => {
                    const value = params.data![key];
                    if (Array.isArray(value)) {
                        value.forEach((item) => formData.append(`${key}[]`, item));
                        return;
                    }
                    formData.append(key, params.data![key]);
                });
            }
            return request({
                url: baseUrl + action,
                method: 'POST',
                data: formData,
                headers: {
                    'Content-Type': 'multipart/form-data;charset=UTF-8',
                    ignoreCancelToken: true,
                },
            }, cancel);
        }
    }
}