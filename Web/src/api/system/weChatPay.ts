import request from '/@/utils/request';
enum Api {
	PagePayList = '/api/sysWechatPay/page',
	CreatePay = '/api/sysWechatPay/payTransactionNative',
	GetRefundListByID = '/api/sysWechatPay/listRefund',
	RefundDomestic = '/api/sysWechatPay/refundDomestic'
}
/**
 * 构造并发送 weChatPay 的 page Pay List 网络请求，随后按接口类型返回响应结果。
 * @param params - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const pagePayList = (params?: any) =>
	request({
		url: Api.PagePayList,
		method: 'post',
		data: params,
	});
/**
 * 构建 weChatPay 所需的 create Pay 内容，并保证生成结果符合调用约定。
 * @param params - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const createPay = (params?: any) =>
	request({
		url: Api.CreatePay,
		method: 'post',
		data: params,
	});
/**
 * 查询或读取 weChatPay 所需的 get Refund List By ID 数据，并按调用约定返回结果。
 * @param params - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getRefundListByID = (params?: any) =>
	request({
		url: Api.GetRefundListByID,
		method: 'post',
		data: params,
	});
/**
 * 构造并发送 weChatPay 的 refund Domestic 网络请求，随后按接口类型返回响应结果。
 * @param params - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const refundDomestic = (params?: any) =>
	request({
		url: Api.RefundDomestic,
		method: 'post',
		data: params,
	});