// 通用函数
import useClipboard from 'vue-clipboard3';
import { ElMessage } from 'element-plus';
import { formatDate } from '/@/utils/formatTime';

export default function () {
	const { toClipboard } = useClipboard();

	// 百分比格式化
	/**
	 * 负责 commonFunction 的 percent Format 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param row - 需要遍历、筛选或写入的业务数据集合。
	 * @param column - 参与映射、显示或数据写入的字段集合。
	 * @param cellValue - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const percentFormat = (row: EmptyArrayType, column: number, cellValue: string) => {
		return cellValue ? `${cellValue}%` : '-';
	};
	// 列表日期时间格式化
	/**
	 * 负责 commonFunction 的 date Format YMD 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param row - 需要遍历、筛选或写入的业务数据集合。
	 * @param column - 参与映射、显示或数据写入的字段集合。
	 * @param cellValue - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const dateFormatYMD = (row: EmptyArrayType, column: number, cellValue: string) => {
		if (!cellValue) return '-';
		return formatDate(new Date(cellValue), 'YYYY-mm-dd');
	};
	// 列表日期时间格式化
	/**
	 * 负责 commonFunction 的 date Format YMDHMS 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param row - 需要遍历、筛选或写入的业务数据集合。
	 * @param column - 参与映射、显示或数据写入的字段集合。
	 * @param cellValue - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const dateFormatYMDHMS = (row: EmptyArrayType, column: number, cellValue: string) => {
		if (!cellValue) return '-';
		return formatDate(new Date(cellValue), 'YYYY-mm-dd HH:MM:SS');
	};
	// 列表日期时间格式化
	/**
	 * 负责 commonFunction 的 date Format HMS 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param row - 需要遍历、筛选或写入的业务数据集合。
	 * @param column - 参与映射、显示或数据写入的字段集合。
	 * @param cellValue - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const dateFormatHMS = (row: EmptyArrayType, column: number, cellValue: string) => {
		if (!cellValue) return '-';
		let time = 0;
		if (typeof row === 'number') time = row;
		if (typeof cellValue === 'number') time = cellValue;
		return formatDate(new Date(time * 1000), 'HH:MM:SS');
	};
	// 小数格式化
	/**
	 * 负责 commonFunction 的 scale Format 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param value - 需要读取、校验或转换的业务值。
	 * @param scale - scale 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const scaleFormat = (value: string = '0', scale: number = 4) => {
		return Number.parseFloat(value).toFixed(scale);
	};
	// 小数格式化
	/**
	 * 负责 commonFunction 的 scale2 Format 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param value - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const scale2Format = (value: string = '0') => {
		return Number.parseFloat(value).toFixed(2);
	};
    // 千分符，默认保留两位小数
	/**
	 * 按 group Separator 条件比较、筛选或整理 commonFunction 数据，并返回顺序稳定的结果。
	 * @param value - 需要读取、校验或转换的业务值。
	 * @param minimumFractionDigits - minimumFractionDigits 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const groupSeparator = (value: number, minimumFractionDigits: number = 2) => {
		return value.toLocaleString('en-US', {
			minimumFractionDigits: minimumFractionDigits,
			maximumFractionDigits: 2,
		});
	};

	/**
	 * 删除字符串首尾指定字符
	 * @param Str 源字符
	 * @param char 去除的指定字符
	 * @param type 类型，右边或左边，为空是替换首尾
	 */
	const trimChar =(Str:string,char:string, type:string) =>{
		if (char) {
			if (type == 'left') {
				return Str.replace(new RegExp('^\\'+char+'+', 'g'), '');
			} else if (type == 'right') {
				return Str.replace(new RegExp('\\'+char+'+$', 'g'), '');
			}
			return Str.replace(new RegExp('^\\'+char+'+|\\'+char+'+$', 'g'), '');
		}
		return Str.replace(/^\s+|\s+$/g, '');
	}
	// 点击复制文本
	/**
	 * 按照 commonFunction 的业务约束完成 copy Text 数据合并或复制。
	 * @param text - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const copyText = (text: string) => {
		return new Promise((resolve, reject) => {
			try {
				//复制
				toClipboard(text);
				//下面可以设置复制成功的提示框等操作
				ElMessage.success(t('message.layout.copyTextSuccess'));
				resolve(text);
			} catch (e) {
				//复制失败
				ElMessage.error(t('message.layout.copyTextError'));
				reject(e);
			}
		});
	};
	// 去掉Html标签(取前面5个字符)
	/**
	 * 移除 commonFunction 中与 remove Html Sub 对应的数据，并清理关联状态。
	 * @param value - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const removeHtmlSub = (value: string) => {
		var str = value.replace(/<[^>]+>/g, '');
		if (str.length > 50) return str.substring(0, 50) + '......';
		else return str;
	};
	// 去掉Html标签
	/**
	 * 移除 commonFunction 中与 remove Html 对应的数据，并清理关联状态。
	 * @param value - 需要读取、校验或转换的业务值。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const removeHtml = (value: string) => {
		return value.replace(/<[^>]+>/g, '');
	};
	// 获取枚举描述
	/**
	 * 查询或读取 commonFunction 所需的 get Enum Desc 数据，并按调用约定返回结果。
	 * @param key - 需要读取、校验或转换的业务值。
	 * @param lstEnum - lstEnum 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const getEnumDesc = (key: any, lstEnum: any) => {
		return lstEnum.find((x: any) => x.value == key)?.describe;
	};
	// 追加query参数到url
	/**
	 * 按照 commonFunction 的业务约束完成 append Query Params 数据合并或复制。
	 * @param url - url 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param params - 本次操作使用的输入数据。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const appendQueryParams = (url: string, params: { [key : string]: any }) => {
		if (!params || Object.keys(params).length == 0) return url;
		const queryString = Object.keys(params).map(key => `${encodeURIComponent(key)}=${encodeURIComponent(params[key])}`).join('&');
		return `${url}${url.includes('?') ? '&' : '?'}${queryString}`;
	};
	return {
		percentFormat,
		dateFormatYMD,
		dateFormatYMDHMS,
		dateFormatHMS,
		scaleFormat,
		scale2Format,
        groupSeparator,
		copyText,
		removeHtmlSub,
		removeHtml,
		getEnumDesc,
		appendQueryParams,
		trimChar,
	};
}
