import { ElMessage } from 'element-plus';

/**
 * 颜色转换函数
 * @method hexToRgb hex 颜色转 rgb 颜色
 * @method rgbToHex rgb 颜色转 Hex 颜色
 * @method getDarkColor 加深颜色值
 * @method getLightColor 变浅颜色值
   * 创建 theme 的 use Change Color 组合式能力，集中暴露状态和操作入口。
  */
export function useChangeColor() {
	// str 颜色值字符串
	/**
	 * 负责 theme 的 hex To Rgb 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param str - str 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const hexToRgb = (str: string): any => {
		let hexs: any = '';
		let reg = /^\#?[0-9A-Fa-f]{6}$/;
		if (!reg.test(str)) {
			ElMessage.warning('输入错误的hex');
			return '';
		}
		str = str.replace('#', '');
		hexs = str.match(/../g);
		for (let i = 0; i < 3; i++) hexs[i] = parseInt(hexs[i], 16);
		return hexs;
	};
	// r 代表红色 | g 代表绿色 | b 代表蓝色
	/**
	 * 负责 theme 的 rgb To Hex 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @param r - r 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param g - g 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param b - b 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const rgbToHex = (r: any, g: any, b: any): string => {
		let reg = /^\d{1,3}$/;
		if (!reg.test(r) || !reg.test(g) || !reg.test(b)) {
			ElMessage.warning('输入错误的rgb颜色值');
			return '';
		}
		let hexs = [r.toString(16), g.toString(16), b.toString(16)];
		for (let i = 0; i < 3; i++) if (hexs[i].length == 1) hexs[i] = `0${hexs[i]}`;
		return `#${hexs.join('')}`;
	};
	// color 颜色值字符串 | level 变浅的程度，限0-1之间
	/**
	 * 查询或读取 theme 所需的 get Dark Color 数据，并按调用约定返回结果。
	 * @param color - color 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param level - level 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const getDarkColor = (color: string, level: number): string => {
		let reg = /^\#?[0-9A-Fa-f]{6}$/;
		if (!reg.test(color)) {
			ElMessage.warning('输入错误的hex颜色值');
			return '';
		}
		let rgb = useChangeColor().hexToRgb(color);
		for (let i = 0; i < 3; i++) rgb[i] = Math.floor(rgb[i] * (1 - level));
		return useChangeColor().rgbToHex(rgb[0], rgb[1], rgb[2]);
	};
	// color 颜色值字符串 | level 加深的程度，限0-1之间
	/**
	 * 查询或读取 theme 所需的 get Light Color 数据，并按调用约定返回结果。
	 * @param color - color 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param level - level 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	const getLightColor = (color: string, level: number): string => {
		let reg = /^\#?[0-9A-Fa-f]{6}$/;
		if (!reg.test(color)) {
			ElMessage.warning('输入错误的hex颜色值');
			return '';
		}
		let rgb = useChangeColor().hexToRgb(color);
		for (let i = 0; i < 3; i++) rgb[i] = Math.floor((255 - rgb[i]) * level + rgb[i]);
		return useChangeColor().rgbToHex(rgb[0], rgb[1], rgb[2]);
	};
	return {
		hexToRgb,
		rgbToHex,
		getDarkColor,
		getLightColor,
	};
}
