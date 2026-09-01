import type { App } from 'vue';
import { useUserInfo } from '/@/stores/userInfo';
import { judgementSameArr } from '/@/utils/arrayOperation';

/**
 * 用户权限指令
 * @directive 单个权限验证（v-auth="xxx"）
 * @directive 多个权限验证，满足一个则显示（v-auths="[xxx,xxx]"）
 * @directive 多个权限验证，全部满足则显示（v-auth-all="[xxx,xxx]"）
   * 按 auth Directive 规则筛选 authDirective 数据，并返回满足条件的结果。
  */
export function authDirective(app: App) {
	// 单个权限验证（v-auth="xxx"）
	app.directive('auth', {
		/**
		 * 在元素挂载后初始化 authDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param binding - 模板字段与工作簿坐标之间的绑定定义。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, binding) {
			const stores = useUserInfo();
			if (!stores.userInfos.authBtnList.some((v: string) => v === binding.value)) el.parentNode.removeChild(el);
		},
	});
	// 多个权限验证，满足一个则显示（v-auths="[xxx,xxx]"）
	app.directive('auths', {
		/**
		 * 在元素挂载后初始化 authDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param binding - 模板字段与工作簿坐标之间的绑定定义。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, binding) {
			let flag = false;
			const stores = useUserInfo();
			stores.userInfos.authBtnList.map((val: string) => {
				binding.value.map((v: string) => {
					if (val === v) flag = true;
				});
			});
			if (!flag) el.parentNode.removeChild(el);
		},
	});
	// 多个权限验证，全部满足则显示（v-auth-all="[xxx,xxx]"）
	app.directive('auth-all', {
		/**
		 * 在元素挂载后初始化 authDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param binding - 模板字段与工作簿坐标之间的绑定定义。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, binding) {
			const stores = useUserInfo();
			const flag = judgementSameArr(binding.value, stores.userInfos.authBtnList);
			if (!flag) el.parentNode.removeChild(el);
		},
	});
}
