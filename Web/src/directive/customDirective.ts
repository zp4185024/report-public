import type { App } from 'vue';

/**
 * 按钮波浪指令
 * @directive 默认方式：v-waves，如 `<div v-waves></div>`
 * @directive 参数方式：v-waves=" |light|red|orange|purple|green|teal"，如 `<div v-waves="'light'"></div>`
   * 初始化 customDirective 的 waves Directive 监听流程，并在状态变化时同步执行相关操作。
  */
export function wavesDirective(app: App) {
	app.directive('waves', {
		/**
		 * 在元素挂载后初始化 customDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param binding - 模板字段与工作簿坐标之间的绑定定义。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, binding) {
			el.classList.add('waves-effect');
			binding.value && el.classList.add(`waves-${binding.value}`);
			/**
			 * 更新 customDirective 的 set Convert Style 状态，并同步相关业务数据。
			 * @param obj - obj 对应的业务输入；具体类型和空值范围以方法签名为准。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			function setConvertStyle(obj: { [key: string]: unknown }) {
				// 第一阶段：构造 wavesDirective 当前阶段需要的中间数据和执行参数。
				let style: string = '';
				for (let i in obj) {
					if (obj.hasOwnProperty(i)) style += `${i}:${obj[i]};`;
				}
				return style;
			}
			/**
			 * 响应 customDirective 的 on Current Click 事件，并协调相关状态和后续操作。
			 * @param e - 触发当前处理流程的界面或业务事件。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			function onCurrentClick(e: { [key: string]: unknown }) {
				let elDiv = document.createElement('div');
				elDiv.classList.add('waves-ripple');
				el.appendChild(elDiv);
				let styles = {
					left: `${e.layerX}px`,
					top: `${e.layerY}px`,
					opacity: 1,
					transform: `scale(${(el.clientWidth / 100) * 10})`,
					'transition-duration': `750ms`,
					'transition-timing-function': `cubic-bezier(0.250, 0.460, 0.450, 0.940)`,
				};
				elDiv.setAttribute('style', setConvertStyle(styles));
				setTimeout(() => {
					// 第2阶段：根据 wavesDirective 当前分支转换输入数据，并保存该阶段产生的中间结果。
					elDiv.setAttribute(
						'style',
						setConvertStyle({
							opacity: 0,
							transform: styles.transform,
							left: styles.left,
							top: styles.top,
						})
					);
					setTimeout(() => {
						elDiv && el.removeChild(elDiv);
					}, 750);
				}, 450);
			}
			el.addEventListener('mousedown', onCurrentClick, false);
		},
		/**
		 * 在元素卸载时释放 customDirective 指令创建的监听器和临时资源。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		unmounted(el) {
			el.addEventListener('mousedown', () => {});
		},
	});
}

/**
 * 自定义拖动指令
 * @description  使用方式：v-drag="[dragDom,dragHeader]"，如 `<div v-drag="['.drag-container .el-dialog', '.drag-container .el-dialog__header']"></div>`
 * @description dragDom 要拖动的元素，dragHeader 要拖动的 Header 位置
 * @link 注意：https://github.com/element-plus/element-plus/issues/522
 * @lick 参考：https://blog.csdn.net/weixin_46391323/article/details/105228020?utm_medium=distribute.pc_relevant.none-task-blog-baidujs_title-10&spm=1001.2101.3001.4242
 */
export function dragDirective(app: App) {
	app.directive('drag', {
		/**
		 * 在元素挂载后初始化 customDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param binding - 模板字段与工作簿坐标之间的绑定定义。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, binding) {
			if (!binding.value) return false;

			const dragDom = document.querySelector(binding.value[0]) as HTMLElement;
			const dragHeader = document.querySelector(binding.value[1]) as HTMLElement;

			/** 鼠标进入拖动区域时切换为移动光标，提示当前元素支持拖拽。 */
			dragHeader.onmouseover = () => (dragHeader.style.cursor = `move`);

			/**
			 * 创建并返回 customDirective 的 down 配置对象，集中提供相关状态和操作入口。
			 * @param e - 触发当前处理流程的界面或业务事件。
			 * @param type - type 对应的业务输入；具体类型和空值范围以方法签名为准。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			function down(e: any, type: string) {
				// 鼠标按下，计算当前元素距离可视区的距离
				const disX = type === 'pc' ? e.clientX - dragHeader.offsetLeft : e.touches[0].clientX - dragHeader.offsetLeft;
				const disY = type === 'pc' ? e.clientY - dragHeader.offsetTop : e.touches[0].clientY - dragHeader.offsetTop;

				// body当前宽度
				const screenWidth = document.body.clientWidth;
				// 可见区域高度(应为body高度，可某些环境下无法获取)
				const screenHeight = document.documentElement.clientHeight;

				// 对话框宽度
				const dragDomWidth = dragDom.offsetWidth;
				// 对话框高度
				const dragDomheight = dragDom.offsetHeight;

				const minDragDomLeft = dragDom.offsetLeft;
				const maxDragDomLeft = screenWidth - dragDom.offsetLeft - dragDomWidth;

				const minDragDomTop = dragDom.offsetTop;
				const maxDragDomTop = screenHeight - dragDom.offsetTop - dragDomheight;

				// 获取到的值带px 正则匹配替换
				let styL: any = getComputedStyle(dragDom).left;
				let styT: any = getComputedStyle(dragDom).top;

				// 注意在ie中 第一次获取到的值为组件自带50% 移动之后赋值为px
				if (styL.includes('%')) {
					styL = +document.body.clientWidth * (+styL.replace(/\%/g, '') / 100);
					styT = +document.body.clientHeight * (+styT.replace(/\%/g, '') / 100);
				} else {
					styL = +styL.replace(/\px/g, '');
					styT = +styT.replace(/\px/g, '');
				}

				return {
					disX,
					disY,
					minDragDomLeft,
					maxDragDomLeft,
					minDragDomTop,
					maxDragDomTop,
					styL,
					styT,
				};
			}

			/**
			 * 根据指针位置更新 customDirective 的 move 坐标，并限制目标元素不超出可视区域。
			 * @param e - 触发当前处理流程的界面或业务事件。
			 * @param type - type 对应的业务输入；具体类型和空值范围以方法签名为准。
			 * @param obj - obj 对应的业务输入；具体类型和空值范围以方法签名为准。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			function move(e: any, type: string, obj: any) {
				let { disX, disY, minDragDomLeft, maxDragDomLeft, minDragDomTop, maxDragDomTop, styL, styT } = obj;

				// 通过事件委托，计算移动的距离
				let left = type === 'pc' ? e.clientX - disX : e.touches[0].clientX - disX;
				let top = type === 'pc' ? e.clientY - disY : e.touches[0].clientY - disY;

				// 边界处理
				if (-left > minDragDomLeft) {
					left = -minDragDomLeft;
				} else if (left > maxDragDomLeft) {
					left = maxDragDomLeft;
				}

				if (-top > minDragDomTop) {
					top = -minDragDomTop;
				} else if (top > maxDragDomTop) {
					top = maxDragDomTop;
				}

				// 移动当前元素
				dragDom.style.cssText += `;left:${left + styL}px;top:${top + styT}px;`;
			}

			/**
			 * pc端
			 * onmousedown 鼠标按下触发事件
			 * onmousemove 鼠标按下时持续触发事件
			 * onmouseup 鼠标抬起触发事件
			 */
			dragHeader.onmousedown = (e) => {
				const obj = down(e, 'pc');
				/** 根据鼠标当前位置持续更新对话框坐标，并限制在可视区域内。 */
				document.onmousemove = (e) => {
					move(e, 'pc', obj);
				};
				/** 鼠标释放后移除本次拖动注册的全局移动与释放处理器。 */
				document.onmouseup = () => {
					document.onmousemove = null;
					document.onmouseup = null;
				};
			};

			/**
			 * 移动端
			 * ontouchstart 当按下手指时，触发ontouchstart
			 * ontouchmove 当移动手指时，触发ontouchmove
			 * ontouchend 当移走手指时，触发ontouchend
			 */
			dragHeader.ontouchstart = (e) => {
				const obj = down(e, 'app');
				/** 根据触摸点当前位置持续更新对话框坐标，并限制在可视区域内。 */
				document.ontouchmove = (e) => {
					move(e, 'app', obj);
				};
				/** 手指离开后移除本次拖动注册的全局触摸处理器。 */
				document.ontouchend = () => {
					document.ontouchmove = null;
					document.ontouchend = null;
				};
			};
		},
	});
}

/**
 * 防止重复点击提交按钮
 * @directive 默认方式：v-reclick，如 `<el-button v-reclick></el-button>`
 * @directive 参数方式：v-reclick="number"，如 `<el-button v-reclick="500"></el-button>`
 */
 export function reclickDirective(app: App) {
	app.directive('reclick', {
		/**
		 * 在元素挂载后初始化 customDirective 指令行为，并注册所需事件或界面状态。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @param time - time 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		mounted(el, time) {
			el.addEventListener('click', () => {
				if (!el.disabled) {
					el.disabled = true;
					setTimeout(
						() => {
							el.disabled = false;
						},
						time.value === undefined ? 500 : time.value
					);
				}
			});
		},
		/**
		 * 在元素卸载时释放 customDirective 指令创建的监听器和临时资源。
		 * @param el - el 对应的业务输入；具体类型和空值范围以方法签名为准。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		unmounted(el) {
			el.disabled = false;
		},
	});
}
