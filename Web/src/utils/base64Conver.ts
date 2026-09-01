/**
 * @description: base64 to blob
   * 创建并返回 base64Conver 的 data URLto Blob 初始数据，避免不同调用方共享可变对象。
  */
export function dataURLtoBlob(base64Buf: string): Blob {
	const arr = base64Buf.split(',');
	const typeItem = arr[0];
	const mime = typeItem.match(/:(.*?);/)![1];
	const bstr = window.atob(arr[1]);
	let n = bstr.length;
	const u8arr = new Uint8Array(n);
	while (n--) {
		u8arr[n] = bstr.charCodeAt(n);
	}
	return new Blob([u8arr], { type: mime });
}

/**
 * img url to base64
 * @param url
 */
export function urlToBase64(url: string, mineType?: string): Promise<string> {
	return new Promise((resolve, reject) => {
		let canvas = document.createElement('CANVAS') as Nullable<HTMLCanvasElement>;
		const ctx = canvas!.getContext('2d');

		const img = new Image();
		img.crossOrigin = '';
		/** 图片加载完成后绘制到 Canvas，并把像素内容编码为 Data URL。 */
		img.onload = function () {
			if (!canvas || !ctx) {
				return reject();
			}
			canvas.height = img.height;
			canvas.width = img.width;
			ctx.drawImage(img, 0, 0);
			const dataURL = canvas.toDataURL(mineType || 'image/png');
			canvas = null;
			resolve(dataURL);
		};
		img.src = url;
	});
}

/**
 * File转Base64
 * @param file
 */
export function fileToBase64(file: Blob) {
	return new Promise((resolve, reject) => {
		const reader = new FileReader();
		reader.readAsDataURL(file);
		/** 文件读取成功后向调用方返回生成的 Data URL。 */
		reader.onload = () => resolve(reader.result);
		/** 文件读取失败时把原始 FileReader 错误交给调用方处理。 */
		reader.onerror = (error) => reject(error);
	});
}

/**
 * Base64转File
 * @param dataURL   {String}  base64
 * @param fileName	{String}  文件名
 * @param mimeType	{String}  [可选]文件类型，默认为base64中的类型
 * @returns {File}
 */
export function base64ToFile(dataURL: string, fileName: string, mimeType = null) {
	var arr = dataURL.split(',');
	var defaultMimeType = arr[0].match(/:(.*?);/)[1];
	var bStr = atob(arr[1]);
	let n = bStr.length;
	var u8arr = new Uint8Array(n);
	while (n--) {
		u8arr[n] = bStr.charCodeAt(n);
	}
	return new File([u8arr], fileName, { type: mimeType || defaultMimeType });
}

/**
 * Blob转File
 * @param blob     {Blob}   blob
 * @param fileName {String} 文件名
 * @param mimeType {String} 文件类型
 * @return {File}
 */
export function blobToFile(blob: Blob, fileName: string, mimeType?: string) {
	if (mimeType == null) mimeType = blob.type;
	return new File([blob], fileName, { type: mimeType });
}
