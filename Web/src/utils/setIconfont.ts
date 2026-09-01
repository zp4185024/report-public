// 字体图标 url
const cssCdnUrlList: Array<string> = [
	// 调整为从本地引入，注释下面的 url
	// '//at.alicdn.com/t/c/font_2298093_rnp72ifj3ba.css',
	// '//netdna.bootstrapcdn.com/font-awesome/4.7.0/css/font-awesome.min.css',
];
// 第三方 js url
const jsCdnUrlList: Array<string> = [];

// 动态批量设置字体图标
/**
 * 更新 setIconfont 的 set Css Cdn 状态，并同步相关业务数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function setCssCdn() {
	if (cssCdnUrlList.length <= 0) return false;
	cssCdnUrlList.map((v) => {
		let link = document.createElement('link');
		link.rel = 'stylesheet';
		link.href = v;
		link.crossOrigin = 'anonymous';
		document.getElementsByTagName('head')[0].appendChild(link);
	});
}

// 动态批量设置第三方js
/**
 * 更新 setIconfont 的 set Js Cdn 状态，并同步相关业务数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function setJsCdn() {
	if (jsCdnUrlList.length <= 0) return false;
	jsCdnUrlList.map((v) => {
		let link = document.createElement('script');
		link.src = v;
		document.body.appendChild(link);
	});
}

/**
 * 批量设置字体图标、动态js
 * @method cssCdn 动态批量设置字体图标
 * @method jsCdn 动态批量设置第三方js
 */
const setIntroduction = {
	// 设置css
	/**
	 * 负责 setIconfont 的 css Cdn 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	cssCdn: () => {
		setCssCdn();
	},
	// 设置js
	/**
	 * 负责 setIconfont 的 js Cdn 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	jsCdn: () => {
		setJsCdn();
	},
};

// 导出函数方法
export default setIntroduction;
