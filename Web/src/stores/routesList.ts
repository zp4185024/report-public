import { defineStore } from 'pinia';

/**
 * 路由列表
 * @methods setRoutesList 设置路由数据
 * @methods setColumnsMenuHover 设置分栏布局菜单鼠标移入 boolean
 * @methods setColumnsNavHover 设置分栏布局最左侧导航鼠标移入 boolean
 */
export const useRoutesList = defineStore('routesList', {
	/**
	 * 创建并返回 routesList 状态仓库的初始数据，供页面和操作方法共享。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	state: (): RoutesListState => ({
		routesList: [],
		isColumnsMenuHover: false,
		isColumnsNavHover: false,
	}),
	actions: {
		/**
		 * 更新 routesList 的 set Routes List 状态，并同步相关业务数据。
		 * @param data - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async setRoutesList(data: Array<string>) {
			this.routesList = data;
		},
		/**
		 * 更新 routesList 的 set Columns Menu Hover 状态，并同步相关业务数据。
		 * @param bool - bool 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async setColumnsMenuHover(bool: Boolean) {
			this.isColumnsMenuHover = bool;
		},
		/**
		 * 更新 routesList 的 set Columns Nav Hover 状态，并同步相关业务数据。
		 * @param bool - bool 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async setColumnsNavHover(bool: Boolean) {
			this.isColumnsNavHover = bool;
		},
	},
});
