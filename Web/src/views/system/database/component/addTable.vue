<template>
	<div class="sys-dbTable-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="1400px">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> 增加表 </span>
				</div>
			</template>
			<el-divider content-position="left">数据表信息</el-divider>
			<el-form :model="state.ruleForm" ref="ruleFormRef" label-width="auto">
				<el-row :gutter="35">
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="表名称" prop="tableName" :rules="[{ required: true, message: '名称不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.tableName" placeholder="表名称" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="描述" prop="description" :rules="[{ required: true, message: '描述不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.description" placeholder="描述" clearable type="textarea" />
						</el-form-item>
					</el-col>
				</el-row>
			</el-form>
			<el-divider content-position="left">数据列信息</el-divider>
			<el-table :data="state.tableData" style="width: 100%" max-height="400">
				<el-table-column prop="dbColumnName" label="字段名" width="200" fixed>
					<template #default="scope">
						<el-input v-model="scope.row.dbColumnName" autocomplete="off" />
					</template>
				</el-table-column>
				<el-table-column prop="columnDescription" label="描述" width="220">
					<template #default="scope">
						<el-input v-model="scope.row.columnDescription" autocomplete="off" />
					</template>
				</el-table-column>
				<el-table-column prop="isPrimarykey" label="主键" width="100">
					<template #default="scope">
						<el-select v-model="scope.row.isPrimarykey" class="m-2" placeholder="Select">
							<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
						</el-select>
					</template>
				</el-table-column>
				<el-table-column prop="isIdentity" label="自增" width="100">
					<template #default="scope">
						<el-select v-model="scope.row.isIdentity" class="m-2" placeholder="Select">
							<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
						</el-select>
					</template>
				</el-table-column>
				<el-table-column prop="dataType" label="类型" width="150">
					<template #default="scope">
						<el-select v-model="scope.row.dataType" class="m-2" placeholder="Select" @change="handleColTypeChange(scope.row)">
							<el-option v-for="item in dataTypeList" :key="item.value" :label="item.value" :value="item.value" />
						</el-select>
					</template>
				</el-table-column>
				<el-table-column prop="isNullable" label="可空" width="100">
					<template #default="scope">
						<el-select v-model="scope.row.isNullable" class="m-2" placeholder="Select">
							<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
						</el-select>
					</template>
				</el-table-column>
				<el-table-column prop="length" label="长度" width="100">
					<template #default="scope">
						<el-input-number v-model="scope.row.length" controls-position="right" class="w100" />
					</template>
				</el-table-column>
				<el-table-column prop="decimalDigits" label="小数位" width="100">
					<template #default="scope">
						<el-input-number v-model="scope.row.decimalDigits" controls-position="right" class="w100" />
					</template>
				</el-table-column>				
				<el-table-column prop="defaultValue" label="默认值" width="90">
					<template #default="scope">
						<el-input v-model="scope.row.defaultValue" autocomplete="off" />
					</template>
				</el-table-column>
				<el-table-column label="操作" min-width="200" align="center" fixed="right">
					<template #default="scope">
						<el-button link type="primary" icon="el-icon-delete" @click.prevent="handleColDelete(scope.$index)">删除</el-button>
						<el-button v-if="state.tableData.length > 1" link type="primary" icon="ele-Top" @click.prevent="handleColUp(scope.row, scope.$index)">上移</el-button>
						<el-button v-if="state.tableData.length > 1" link type="primary" icon="ele-Bottom" @click.prevent="handleColDown(scope.row, scope.$index)">下移</el-button>
					</template>
				</el-table-column>
			</el-table>
			<div style="text-align: left; margin-top: 10px">
				<el-button icon="ele-Plus" @click="addPrimaryColumn">新增主键字段</el-button>
				<el-button icon="ele-Plus" @click="addColumn">新增普通字段</el-button>
				<el-button icon="ele-Plus" @click="addTenantColumn">新增租户字段</el-button>
				<el-button icon="ele-Plus" @click="addOrgColumn">新增机构字段</el-button>
				<el-button icon="ele-Plus" @click="addBaseColumn">新增基础字段</el-button>
				<el-button icon="ele-Plus" @click="addDeleteColumn">新增软删除字段</el-button>
			</div>

			<template #footer>
				<span class="dialog-footer">
					<el-button @click="cancel">取 消</el-button>
					<el-button type="primary" @click="submit">确 定</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script lang="ts" setup name="sysAddTable">
import { reactive, ref } from 'vue';
import { ElMessage } from 'element-plus';

import { getAPI } from '/@/utils/axios-utils';
import { SysDatabaseApi } from '/@/api-services/api';
import { UpdateDbTableInput } from '/@/api-services/models';
import { dataTypeList, EditRecordRow, yesNoSelect } from '../database';

var colIndex = 0;
const emits = defineEmits(['addTableSubmitted']);
const ruleFormRef = ref();
const state = reactive({
	isShowDialog: false,
	ruleForm: {} as UpdateDbTableInput,
	tableData: [] as any,
});

// 打开弹窗
/**
 * 准备 addTable 的 open Dialog 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = (row: any) => {
	state.ruleForm = row;
	state.isShowDialog = true;
	ruleFormRef.value?.resetFields();
};

// 关闭弹窗
/**
 * 结束 addTable 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
	emits('addTableSubmitted', state.ruleForm.tableName ?? '');
	state.tableData = [];
	state.isShowDialog = false;
};

// 取消
/**
 * 校验 addTable 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

// 提交
/**
 * 校验当前输入并提交保存请求，成功后同步 addTable 的界面状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const submit = () => {
	ruleFormRef.value.validate(async (valid: boolean) => {
		if (!valid) return;
		if (state.tableData.length === 0) {
			ElMessage({
				type: 'error',
				message: `请添加列!`,
			});
			return;
		}
		const params: any = {
			dbColumnInfoList: state.tableData,
			...state.ruleForm,
		};
		await getAPI(SysDatabaseApi).apiSysDatabaseAddTablePost(params);
		closeDialog();
	});
};

// 增加主键列
/**
 * 构建 addTable 所需的 add Primary Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addPrimaryColumn() {
	state.tableData.push({
		columnDescription: '主键Id',
		dataType: 'bigint',
		dbColumnName: 'Id',
		decimalDigits: 0,
		isIdentity: 0,
		isNullable: 0,
		isPrimarykey: 1,
		length: 0,
		key: colIndex,
		editable: true,
		isNew: true,
	});
	colIndex++;
}

// 增加普通列
/**
 * 构建 addTable 所需的 add Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addColumn() {
	state.tableData.push({
		columnDescription: '',
		//dataType: 'varchar',
		dbColumnName: '',
		decimalDigits: 0,
		isIdentity: 0,
		isNullable: 1,
		isPrimarykey: 0,
		//length: 32,
		key: colIndex,
		editable: true,
		isNew: true,
	});
	colIndex++;
}

// 增加租户列
/**
 * 构建 addTable 所需的 add Tenant Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addTenantColumn() {
	state.tableData.push({
		columnDescription: '租户Id',
		dataType: 'bigint',
		dbColumnName: 'TenantId',
		decimalDigits: 0,
		isIdentity: 0,
		isNullable: 1,
		isPrimarykey: 0,
		length: 0,
		key: colIndex,
		editable: true,
		isNew: true,
	});
	colIndex++;
}

// 增加机构列
/**
 * 构建 addTable 所需的 add Org Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addOrgColumn() {
	state.tableData.push({
		columnDescription: '机构Id',
		dataType: 'bigint',
		dbColumnName: 'OrgId',
		decimalDigits: 0,
		isIdentity: 0,
		isNullable: 1,
		isPrimarykey: 0,
		length: 0,
		key: colIndex,
		editable: true,
		isNew: true,
	});
	colIndex++;
}

// 增加通用基础列
/**
 * 构建 addTable 所需的 add Base Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addBaseColumn() {
	// 第一阶段：构造 addBaseColumn 当前阶段需要的中间数据和执行参数。
	const fileds = [
		{
			dataType: 'datetime',
			name: 'CreateTime',
			desc: '创建时间',
		},
		{
			dataType: 'datetime',
			name: 'UpdateTime',
			desc: '更新时间',
		},
		{
			dataType: 'bigint',
			name: 'CreateUserId',
			desc: '创建者Id',
		},
		{
			dataType: 'varchar',
			name: 'CreateUserName',
			desc: '创建者姓名',
			length: 64,
		},
		{
			dataType: 'bigint',
			name: 'UpdateUserId',
			desc: '修改者Id',
		},
		{
			dataType: 'varchar',
			name: 'UpdateUserName',
			desc: '修改者姓名',
			length: 64,
		},
	];

	// 第2阶段：遍历 addBaseColumn 当前数据集合，逐项转换并维护处理结果。
	fileds.forEach((m: any) => {
		state.tableData.push({
			columnDescription: m.desc,
			dataType: m.dataType,
			dbColumnName: m.name,
			decimalDigits: 0,
			isIdentity: 0,
			isNullable: m.isNullable === 0 ? 0 : 1,
			isPrimarykey: 0,
			length: m.length || 0,
			key: colIndex,
			editable: true,
			isNew: true,
		});
		colIndex++;
	});
}

// 增加软删除列
/**
 * 构建 addTable 所需的 add Delete Column 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function addDeleteColumn() {
	state.tableData.push({
		columnDescription: '软删除',
		dataType: 'bit',
		dbColumnName: 'IsDelete',
		decimalDigits: 0,
		isIdentity: 0,
		isNullable: 0,
		isPrimarykey: 0,
		length: 0,
		key: colIndex,
		editable: true,
		isNew: true,
	});
	colIndex++;
}

/**
 * 响应 addTable 的 handle Col Delete 事件，并协调相关状态和后续操作。
 * @param index - 控制数据范围或位置的数值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function handleColDelete(index: number) {
	state.tableData.splice(index, 1);
}

// 列类型选择变化
/**
 * 响应 addTable 的 handle Col Type Change 事件，并协调相关状态和后续操作。
 * @param record - record 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function handleColTypeChange(record: EditRecordRow) {
    if (['varchar', 'char', 'nvarchar', 'nchar'].includes(record.dataType as string)) {
        if ([0, undefined, null].includes(record.length)) {
            record.length = 32;
        }
    } else {
        record.length = 0;
    }
}

// 上移
/**
 * 响应 addTable 的 handle Col Up 事件，并协调相关状态和后续操作。
 * @param record - record 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param index - 控制数据范围或位置的数值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function handleColUp(record: EditRecordRow, index: number) {
	if (record.isNew) {
		var data1 = ChangeExForArray(index, index - 1, state.tableData);
		return data1;
	}
}

// 下移
/**
 * 响应 addTable 的 handle Col Down 事件，并协调相关状态和后续操作。
 * @param record - record 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param index - 控制数据范围或位置的数值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function handleColDown(record: EditRecordRow, index: number) {
	if (record.isNew) {
		return ChangeExForArray(index, index + 1, state.tableData);
	}
}

/**
 * 更新 addTable 的 Change Ex For Array 状态，并同步相关业务数据。
 * @param index1 - index1 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param index2 - index2 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param array - array 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function ChangeExForArray(index1: number, index2: number, array: Array<EditRecordRow>) {
	let maxIndex = state.tableData.length - 1; //最大索引
	if (index2 > maxIndex) {
		index2 = 0;
	}
	if (index2 < 0) {
		index2 = maxIndex;
	}
	let temp = array[index1];
	array[index1] = array[index2];
	array[index2] = temp;
	return array;
}

// 导出对象
defineExpose({ openDialog });
</script>
