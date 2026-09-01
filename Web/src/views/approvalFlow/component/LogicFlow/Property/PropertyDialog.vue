<template>
	<div class="property-dialog">
		<PropertyCommon :nodeData="nodeData" @setProperties="setProperties"></PropertyCommon>
	</div>
</template>

<script setup>
import { reactive, ref } from 'vue';

import PropertyCommon from '/@/views/approvalFlow/component/LogicFlow/Property/PropertyCommon.vue';

var props = defineProps({
	lf: Object,
	nodeData: Object,
});
const emit = defineEmits(['setPropertiesFinish']);

const ruleFormRef = ref();

const state = reactive({});

/**
 * 更新 PropertyDialog 的 set Properties 状态，并同步相关业务数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const setProperties = () => {};
</script>

<style lang="scss" scoped>
.property-dialog {
	padding-left: 20px;
	padding-right: 20px;
}
</style>
