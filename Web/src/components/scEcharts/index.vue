<template>
	<div ref="scEcharts" :style="{ height: height, width: width }"></div>
</template>

<script>
import * as echarts from 'echarts';
import T from './echarts-theme-T.js';

echarts.registerTheme('T', T);
/**
 * 负责 index 的 unwarp 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param obj - obj 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const unwarp = (obj) => obj && (obj.__v_raw || obj.valueOf() || obj);

export default {
	...echarts,
	name: 'scEcharts',
	props: {
		height: { type: String, default: '100%' },
		width: { type: String, default: '100%' },
		nodata: { type: Boolean, default: false },
		option: {
			type: Object,
			/**
			 * 为 option 属性创建独立默认值，避免组件实例之间共享可变数据。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			default: () => {},
		},
	},
	/**
	 * 创建并返回 index 的 data 配置对象，集中提供相关状态和操作入口。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	data() {
		return {
			isActivat: false,
			myChart: null,
		};
	},
	watch: {
		option: {
			deep: true,
			/**
			 * 响应 index 的 handler 事件，并协调相关状态和后续操作。
			 * @param v - v 对应的业务输入；具体类型和空值范围以方法签名为准。
			 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
			 */
			handler(v) {
				this.$nextTick(() => {
					unwarp(this.myChart).setOption(v);
				});
			},
		},
	},
	computed: {
		/**
		 * 负责 index 的 my Options 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		myOptions: function () {
			return this.option || {};
		},
	},
	/**
	 * 负责 index 的 activated 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	activated() {
		if (!this.isActivat) {
			this.$nextTick(() => {
				this.myChart.resize();
			});
		}
	},
	/**
	 * 负责 index 的 deactivated 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	deactivated() {
		this.isActivat = false;
	},
	/**
	 * 在元素挂载后初始化 index 指令行为，并注册所需事件或界面状态。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	mounted() {
		this.isActivat = true;
		this.$nextTick(() => {
			this.draw();
		});
	},
	methods: {
		/**
		 * 初始化 index 的 draw 监听流程，并在状态变化时同步执行相关操作。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		draw() {
			let myChart = echarts.init(this.$refs.scEcharts, 'T');
			myChart.setOption(this.myOptions);
			this.myChart = myChart;
			window.addEventListener('resize', () => myChart.resize());
		},
	},
};
</script>
