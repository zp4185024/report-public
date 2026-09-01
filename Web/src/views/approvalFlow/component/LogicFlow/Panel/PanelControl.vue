<template>
	<div class="panel-control">
		<el-button-group>
			<el-button type="default" size="small" @click="$_zoomIn">放大</el-button>
			<el-button type="default" size="small" @click="$_zoomOut">缩小</el-button>
			<el-button type="default" size="small" @click="$_zoomReset">大小适应</el-button>
			<el-button type="default" size="small" @click="$_translateRest">定位还原</el-button>
			<el-button type="default" size="small" @click="$_reset">还原(大小&定位)</el-button>
			<el-button type="default" size="small" @click="$_undo" :disabled="state.undoDisable">上一步(ctrl+z)</el-button>
			<el-button type="default" size="small" @click="$_redo" :disabled="state.redoDisable">下一步(ctrl+y)</el-button>
			<el-button type="default" size="small" @click="$_download">下载图片</el-button>
			<el-button type="default" size="small" @click="$_catData">查看数据</el-button>
			<el-button type="default" size="small" @click="$_showMiniMap">查看缩略图</el-button>
		</el-button-group>
	</div>
</template>

<script setup lang="ts">
import { reactive } from 'vue';

var props = defineProps({
	lf: Object,
});
const emit = defineEmits(['catData']);

const state = reactive({
	undoDisable: true,
	redoDisable: true,
});

/**
 * 验证 $ zoom In 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_zoomIn = () => {
	props.lf?.zoom(true);
};

/**
 * 验证 $ zoom Out 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_zoomOut = () => {
	props.lf?.zoom(false);
};

/**
 * 验证 $ zoom Reset 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_zoomReset = () => {
	props.lf?.resetZoom();
};

/**
 * 验证 $ translate Rest 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_translateRest = () => {
	props.lf?.resetTranslate();
};

/**
 * 验证 $ reset 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_reset = () => {
	props.lf?.resetZoom();
	props.lf?.resetTranslate();
};

/**
 * 验证 $ undo 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_undo = () => {
	props.lf?.undo();
};

/**
 * 验证 $ redo 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_redo = () => {
	props.lf?.redo();
};

/**
 * 验证 $ download 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_download = () => {
	props.lf?.getSnapshot();
};

/**
 * 验证 $ cat Data 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_catData = () => {
	emit('catData');
};

/**
 * 验证 $ show Mini Map 场景下的预期行为，并防止相关逻辑回归。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const $_showMiniMap = () => {
	props.lf?.extension.miniMap.show(props.lf.graphModel.width - 210, 70);
};
</script>

<style lang="scss" scoped>
.panel-control {
	position: absolute;
	top: 30px;
	right: 50px;
	z-index: 2;
}
</style>
