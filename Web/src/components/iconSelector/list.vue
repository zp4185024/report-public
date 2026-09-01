<template>
	<div class="icon-selector-warp-row">
		<el-scrollbar ref="selectorScrollbarRef">
			<el-row :gutter="10" v-if="props.list.length > 0">
				<el-col :xs="6" :sm="4" :md="4" :lg="4" :xl="4" v-for="(v, k) in list" :key="k" @click="onColClick(v)">
					<div class="icon-selector-warp-item" :class="{ 'icon-selector-active': prefix === v }">
						<SvgIcon :name="v" />
					</div>
				</el-col>
			</el-row>
			<el-empty :image-size="100" v-if="list.length <= 0" :description="empty"></el-empty>
		</el-scrollbar>
	</div>
</template>

<script setup lang="ts" name="iconSelectorList">
// 定义父组件传过来的值
const props = defineProps({
	// 图标列表数据
	list: {
		type: Array,
		/**
		 * 为 list 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => [],
	},
	// 自定义空状态描述文字
	empty: {
		type: String,
		/**
		 * 为 empty 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '无相关图标',
	},
	// 高亮当前选中图标
	prefix: {
		type: String,
		/**
		 * 为 prefix 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '',
	},
});

// 定义子组件向父组件传值/事件
const emit = defineEmits(['get-icon']);

// 当前 icon 图标点击时
/**
 * 响应 list 的 on Col Click 事件，并协调相关状态和后续操作。
 * @param v - v 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onColClick = (v: unknown | string) => {
	emit('get-icon', v);
};
</script>

<style scoped lang="scss">
.icon-selector-warp-row {
	height: 230px;
	overflow: hidden;
	.el-row {
		padding: 15px;
	}
	.el-scrollbar__bar.is-horizontal {
		display: none;
	}
	.icon-selector-warp-item {
		display: flex;
		justify-content: center;
		align-items: center;
		border: 1px solid var(--el-border-color);
		border-radius: 5px;
		margin-bottom: 10px;
		height: 30px;
        width: 30px;
		i {
            display: inline-flex;
			font-size: 20px;
			color: var(--el-text-color-regular);
		}
		&:hover {
			cursor: pointer;
			background-color: var(--el-color-primary-light-9);
			border: 1px solid var(--el-color-primary-light-5);
			i {
				color: var(--el-color-primary);
			}
		}
	}
	.icon-selector-active {
		background-color: var(--el-color-primary-light-9);
		border: 1px solid var(--el-color-primary-light-5);
		i {
			color: var(--el-color-primary);
		}
	}
}
</style>
