import request from '/@/utils/request';

/**
 * 以下为模拟接口地址，gitee 的不通，就换自己的真实接口地址
 *
 * （不建议写成 request.post(xxx)，因为这样 post 时，无法 params 与 data 同时传参）
 *
 * 后端控制菜单模拟json，路径在 https://gitee.com/lyt-top/vue-next-admin-images/tree/master/menu
 * 后端控制路由，isRequestRoutes 为 true，则开启后端控制路由
 * @method getAdminMenu 获取后端动态路由菜单(admin)
 * @method getTestMenu 获取后端动态路由菜单(test)
 */
export function useMenuApi() {
	return {
		/**
		 * 查询或读取 index 所需的 get Admin Menu 数据，并按调用约定返回结果。
		 * @param params - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		getAdminMenu: (params?: object) => {
			return request({
				url: '/gitee/lyt-top/vue-next-admin-images/raw/master/menu/adminMenu.json',
				method: 'get',
				params,
			});
		},
		/**
		 * 查询或读取 index 所需的 get Test Menu 数据，并按调用约定返回结果。
		 * @param params - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		getTestMenu: (params?: object) => {
			return request({
				url: '/gitee/lyt-top/vue-next-admin-images/raw/master/menu/testMenu.json',
				method: 'get',
				params,
			});
		},
	};
}
