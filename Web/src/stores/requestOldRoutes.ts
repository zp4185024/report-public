import { defineStore } from 'pinia';

/**
 * 后端返回原始路由(未处理时)
 * @methods setCacheKeepAlive 设置接口原始路由数据
 */
export const useRequestOldRoutes = defineStore('requestOldRoutes', {
	/**
	 * 创建并返回 requestOldRoutes 状态仓库的初始数据，供页面和操作方法共享。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	state: (): RequestOldRoutesState => ({
		requestOldRoutes: [],
	}),
	actions: {
		/**
		 * 更新 requestOldRoutes 的 set Request Old Routes 状态，并同步相关业务数据。
		 * @param routes - routes 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async setRequestOldRoutes(routes: Array<string>) {
			this.requestOldRoutes = routes;
		},
	},
});
