<template>
	<el-card shadow="hover" header="实时收入" v-loading="loading">
		<scEcharts ref="c1" height="300px" :option="option"></scEcharts>
	</el-card>
</template>

<script>
import scEcharts from '/@/components/scEcharts/index.vue';

export default {
	title: '实时收入',
	icon: 'ele-DataLine',
	description: 'Echarts组件演示',
	components: {
		scEcharts,
	},
	/**
	 * 创建并返回 echarts 的 data 配置对象，集中提供相关状态和操作入口。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	data() {
		return {
			loading: true,
			option: {},
		};
	},
	/**
	 * 构建 echarts 所需的 created 内容，并保证生成结果符合调用约定。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	created() {
		var _this = this;
		setTimeout(function () {
			_this.loading = false;
		}, 500);

		// 第一阶段：构造 created 当前阶段需要的中间数据和执行参数。
		var option = {
			tooltip: {
				trigger: 'axis',
			},
			xAxis: {
				boundaryGap: false,
				type: 'category',
				data: (function () {
					var now = new Date();
					var res = [];
					var len = 30;
					while (len--) {
						res.unshift(now.toLocaleTimeString().replace(/^\D*/, ''));
						now = new Date(now - 2000);
					}
					return res;
				})(),
			},
			yAxis: [
				{
					type: 'value',
					name: '价格',
					splitLine: {
						show: false,
					},
				},
			],
			series: [
				{
					name: '收入',
					type: 'line',
					symbol: 'none',
					lineStyle: {
						width: 1,
						color: '#409EFF',
					},
					areaStyle: {
						opacity: 0.1,
						color: '#79bbff',
					},
					data: (function () {
						// 第2阶段：构造 created 当前阶段需要的中间数据和执行参数。
						var res = [];
						var len = 30;
						while (len--) {
							res.push(Math.round(Math.random() * 0));
						}
						return res;
					})(),
				},
			],
		};
		this.option = option;
	},
	/**
	 * 在元素挂载后初始化 echarts 指令行为，并注册所需事件或界面状态。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	mounted() {
		var _this = this;
		setInterval(function () {
			var o = _this.option;

			o.series[0].data.shift();
			o.series[0].data.push(Math.round(Math.random() * 100));

			o.xAxis.data.shift();
			o.xAxis.data.push(new Date().toLocaleTimeString().replace(/^\D*/, ''));

			//_this.$refs.c1.myChart.setOption(o)
		}, 2100);
	},
};
</script>
