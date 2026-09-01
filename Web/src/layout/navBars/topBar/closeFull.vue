<template>
	<div class="layout-navbars-close-full" v-if="isTagsViewCurrenFull">
		<div class="layout-navbars-close-full-icon">
			<SvgIcon name="ele-Close" title="关闭全屏" @click="onCloseFullscreen" />
		</div>
	</div>
</template>

<script setup lang="ts" name="layoutCloseFull">
import { storeToRefs } from 'pinia';
import { useTagsViewRoutes } from '/@/stores/tagsViewRoutes';

// 定义变量内容
const stores = useTagsViewRoutes();
const { isTagsViewCurrenFull } = storeToRefs(stores);

// 关闭当前全屏
/**
 * 响应 closeFull 的 on Close Fullscreen 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onCloseFullscreen = () => {
	stores.setCurrenFullscreen(false);
};
</script>

<style scoped lang="scss">
.layout-navbars-close-full {
	position: fixed;
	z-index: 9999999999;
	right: -30px;
	top: -30px;
	.layout-navbars-close-full-icon {
		width: 60px;
		height: 60px;
		border-radius: 100%;
		cursor: pointer;
		background: rgba(0, 0, 0, 0.1);
		transition: all 0.3s ease;
		position: relative;
		:deep(i) {
			position: absolute;
			left: 10px;
			top: 35px;
			color: #333333;
			transition: all 0.3s ease;
		}
	}
	&:hover {
		transition: all 0.3s ease;
		:deep(i) {
			color: var(--el-color-primary);
			transition: all 0.3s ease;
		}
	}
}
</style>
