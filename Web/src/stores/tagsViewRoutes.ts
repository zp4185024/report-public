import { defineStore } from 'pinia';
import { Session } from '/@/utils/storage';

/**
 * TagsView 路由列表
 * @methods setTagsViewRoutes 设置 TagsView 路由列表
 * @methods setCurrenFullscreen 设置开启/关闭全屏时的 boolean 状态
 */
export const useTagsViewRoutes = defineStore('tagsViewRoutes', {
	/**
	 * 创建并返回 tagsViewRoutes 状态仓库的初始数据，供页面和操作方法共享。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	state: (): TagsViewRoutesState => ({
		tagsViewRoutes: [],
		isTagsViewCurrenFull: false,
	}),
	actions: {
		/**
		 * 更新 tagsViewRoutes 的 set Tags View Routes 状态，并同步相关业务数据。
		 * @param data - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async setTagsViewRoutes(data: Array<string>) {
			this.tagsViewRoutes = data;
		},
		/**
		 * 更新 tagsViewRoutes 的 set Curren Fullscreen 状态，并同步相关业务数据。
		 * @param bool - bool 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		setCurrenFullscreen(bool: Boolean) {
			Session.set('isTagsViewCurrenFull', bool);
			this.isTagsViewCurrenFull = bool;
		},
	},
});
