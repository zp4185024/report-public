import request from '/@/utils/request';

/**
 * （不建议写成 request.post(xxx)，因为这样 post 时，无法 params 与 data 同时传参）
 *
 * 登录api接口集合
 * @method signIn 用户登录
 * @method signOut 用户退出登录
 */
export function useLoginApi() {
	return {
		/**
		 * 构造并发送 index 的 sign In 网络请求，随后按接口类型返回响应结果。
		 * @param data - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		signIn: (data: object) => {
			return request({
				url: '/user/signIn',
				method: 'post',
				data,
			});
		},
		/**
		 * 构造并发送 index 的 sign Out 网络请求，随后按接口类型返回响应结果。
		 * @param data - 本次操作使用的输入数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		signOut: (data: object) => {
			return request({
				url: '/user/signOut',
				method: 'post',
				data,
			});
		},
	};
}
