import { defineStore } from 'pinia';
import { getAPI } from '/@/utils/axios-utils';
import { SysLangApi } from '/@/api-services/api';

export const useLangStore = defineStore('lang', {
	/**
	 * 创建并返回 useLangStore 状态仓库的初始数据，供页面和操作方法共享。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	state: () => ({
		languages: [] as any[],
	}),
	actions: {
		/**
		 * 查询或读取 useLangStore 所需的 load Languages 数据，并按调用约定返回结果。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		async loadLanguages() {
			if (this.languages.length === 0) {
				const res = await getAPI(SysLangApi).apiSysLangDropdownDataPost();
				this.languages = res.data.result ?? [];
			}
		},
	},
});
