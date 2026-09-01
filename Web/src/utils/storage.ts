import Cookies from 'js-cookie';

/**
 * window.localStorage 浏览器永久缓存
 * @method set 设置永久缓存
 * @method get 获取永久缓存
 * @method remove 移除永久缓存
 * @method clear 移除全部永久缓存
 */
export const Local = {
	// 查看 v2.4.3版本更新日志
	/**
	 * 更新 storage 的 set Key 状态，并同步相关业务数据。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	setKey(key: string) {
		// @ts-ignore
		return `${__NEXT_NAME__}:${key}`;
	},
	// 设置永久缓存
	/**
	 * 更新 storage 的 set 状态，并同步相关业务数据。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	set<T>(key: string, val: T) {
		window.localStorage.setItem(Local.setKey(key), JSON.stringify(val));
	},
	// 获取永久缓存
	/**
	 * 查询或读取 storage 所需的 get 数据，并按调用约定返回结果。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	get(key: string) {
		let json = <string>window.localStorage.getItem(Local.setKey(key));
		return JSON.parse(json);
	},
	// 移除永久缓存
	/**
	 * 移除 storage 中与 remove 对应的数据，并清理关联状态。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	remove(key: string) {
		window.localStorage.removeItem(Local.setKey(key));
	},
	// 移除全部永久缓存
	/**
	 * 移除 storage 中与 clear 对应的数据，并清理关联状态。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	clear() {
		window.localStorage.clear();
	},
};

/**
 * window.sessionStorage 浏览器临时缓存
 * @method set 设置临时缓存
 * @method get 获取临时缓存
 * @method remove 移除临时缓存
 * @method clear 移除全部临时缓存
 */
export const Session = {
	// 设置临时缓存
	/**
	 * 更新 storage 的 set 状态，并同步相关业务数据。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	set<T>(key: string, val: T) {
		if (key === 'token') return Cookies.set(key, val);
		window.sessionStorage.setItem(Local.setKey(key), JSON.stringify(val));
	},
	// 获取临时缓存
	/**
	 * 查询或读取 storage 所需的 get 数据，并按调用约定返回结果。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	get(key: string) {
		if (key === 'token') return Cookies.get(key);
		let json = <string>window.sessionStorage.getItem(Local.setKey(key));
		return JSON.parse(json);
	},
	// 移除临时缓存
	/**
	 * 移除 storage 中与 remove 对应的数据，并清理关联状态。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	remove(key: string) {
		if (key === 'token') return Cookies.remove(key);
		window.sessionStorage.removeItem(Local.setKey(key));
	},
	// 移除全部临时缓存
	/**
	 * 移除 storage 中与 clear 对应的数据，并清理关联状态。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	clear() {
		Cookies.remove('token');
		Cookies.remove('userInfo');
		Cookies.remove('constList');
		window.sessionStorage.clear();
	},
};
