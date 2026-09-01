<!--可编辑表格 
  1.实现表格内编辑并同步数据到父组件(emit)；
  2. 支持自由录入和下拉选择 
  3.内置删除、移动功能 
  4. 支持任意业务模型
  使用注意：1. 业务模型需要id列,以支持删除 2.下拉选项用数组[label :string,value :any]输入 3. 父组件需要update:modelValue，并将父组件的数据更新
  -->
<template>
	<el-table :data="localData" border="true" stripe="true">
		<el-table-column v-for="col in columns" :key="col.id" :label="col.label" :prop="col.prop" :width="col.width">
			<template #default="{ row }">
				<template v-if="isEditable(col)">
					<el-input v-model="row[col.prop]" v-if="col.type === 'input'" />
					<el-select v-model="row[col.prop]" v-if="col.type === 'select'">
						<el-option v-for="item in col.options" :key="item.value" :label="item.label" :value="item.value" />
					</el-select>
				</template>
				<template v-else>
					{{ row[col.prop] }}
				</template>
			</template>
		</el-table-column>
		<el-table-column label="操作" width="75">
			<template #default="{ row }">
				<el-button-group>
					<el-button text type="danger" :icon="Delete" @click="removeUser(row.id)"></el-button>
					<el-button text :icon="ArrowUp" @click="() => moveUser(row.id, 'up')" :disabled="isFirstRow(row)"></el-button>
					<el-button text :icon="ArrowDown" @click="() => moveUser(row.id, 'down')" :disabled="isLastRow(row)"></el-button>
				</el-button-group>
			</template>
		</el-table-column>
	</el-table>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue';
import { EditableColumn } from './editableColumn';
import { ArrowUp, ArrowDown, Delete } from '@element-plus/icons-vue';

const props = defineProps({
	columns: {
		type: Array as () => EditableColumn[],
		required: true,
	},
	modelValue: {
		type: Array as () => Array<Record<string, any>>,
		required: true,
	},
});

const emit = defineEmits(['update:modelValue']);
const localData = ref(props.modelValue);

/**
 * 移除 editableTable 中与 remove User 对应的数据，并清理关联状态。
 * @param id - 需要定位的业务记录标识。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const removeUser = (id: number) => {
	localData.value = localData.value.filter((user) => user.id !== id);
	// console.log('after remove:', localData.value);
	emit('update:modelValue', localData.value);
};

/**
 * 根据指针位置更新 editableTable 的 move User 坐标，并限制目标元素不超出可视区域。
 * @param id - 需要定位的业务记录标识。
 * @param direction - direction 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const moveUser = (id: number, direction: 'up' | 'down') => {
	const index = localData.value.findIndex((user) => user.id === id);
	if (direction === 'up' && index > 0) {
		[localData.value[index], localData.value[index - 1]] = [localData.value[index - 1], localData.value[index]];
	} else if (direction === 'down' && index < localData.value.length - 1) {
		[localData.value[index], localData.value[index + 1]] = [localData.value[index + 1], localData.value[index]];
	}
};
watch(
	localData,
	(val, oldVal) => {
		// console.log('new: ', val);
		// console.log('old:', oldVal);
		// console.log('props.data: ', props.modelValue);
	},
	{ deep: true }
);
/**
 * 校验 editableTable 的 is Editable 条件，并返回可供后续流程判断的结果。
 * @param col - col 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const isEditable = (col: EditableColumn) => col.editable !== false;
/**
 * 校验 editableTable 的 is First Row 条件，并返回可供后续流程判断的结果。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const isFirstRow = (row: Record<string, any>) => localData.value[0].id === row.id;
/**
 * 校验 editableTable 的 is Last Row 条件，并返回可供后续流程判断的结果。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const isLastRow = (row: Record<string, any>) => localData.value[localData.value.length - 1].id === row.id;
</script>

<style scoped></style>
