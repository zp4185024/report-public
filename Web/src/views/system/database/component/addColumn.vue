<template>
	<div class="sys-dbColumn-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="700px">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> 增加列 </span>
				</div>
			</template>
			<el-form :model="state.ruleForm" ref="ruleFormRef" label-width="auto">
				<el-row :gutter="35">
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="列名" prop="dbColumnName" :rules="[{ required: true, message: '名称不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.dbColumnName" placeholder="列名称" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="描述" prop="columnDescription" :rules="[{ required: true, message: '描述不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.columnDescription" placeholder="描述" clearable type="textarea" />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="主键">
							<el-select v-model="state.ruleForm.isPrimarykey" class="w100">
								<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
							</el-select>
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="自增">
							<el-select v-model="state.ruleForm.isIdentity" class="w100">
								<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
							</el-select>
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="类型">
							<el-select v-model="state.ruleForm.dataType" class="w100">
								<el-option v-for="item in dataTypeList" :key="item.value" :label="item.value" :value="item.value" />
							</el-select>
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="可空">
							<el-select v-model="state.ruleForm.isNullable" class="w100">
								<el-option v-for="item in yesNoSelect" :key="item.value" :label="item.label" :value="item.value" />
							</el-select>
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="长度">
							<el-input-number v-model="state.ruleForm.length" class="w100" controls-position="right" />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="小数位">
							<el-input-number v-model="state.ruleForm.decimalDigits" class="w100" controls-position="right" />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="默认值" prop="defaultValue">
							<el-input v-model="state.ruleForm.defaultValue" placeholder="默认值" clearable />
						</el-form-item>
					</el-col>
				</el-row>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="cancel">取 消</el-button>
					<el-button type="primary" @click="submit">确 定</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script lang="ts" setup name="sysAddColumn">
import { reactive, ref } from 'vue';

import { getAPI } from '/@/utils/axios-utils';
import { SysDatabaseApi } from '/@/api-services/api';
import { DbColumnInput } from '/@/api-services/models';
import { dataTypeList, yesNoSelect } from '../database';

const emits = defineEmits(['handleQueryColumn']);
const ruleFormRef = ref();
const state = reactive({
	isShowDialog: false,
	ruleForm: {} as DbColumnInput,
});

// 打开弹窗
/**
 * 准备 addColumn 的 open Dialog 交互状态并打开对应界面。
 * @param addRow - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = (addRow: DbColumnInput) => {
	state.ruleForm = addRow;
	if (state.ruleForm.length === 0) {
		state.ruleForm.length = 32;
	}
	state.isShowDialog = true;
	ruleFormRef.value?.resetFields();
};

// 关闭弹窗
/**
 * 结束 addColumn 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
	emits('handleQueryColumn');
	state.isShowDialog = false;
};

// 取消
/**
 * 校验 addColumn 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

// 提交
/**
 * 校验当前输入并提交保存请求，成功后同步 addColumn 的界面状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const submit = () => {
	ruleFormRef.value.validate(async (valid: boolean) => {
		if (!valid) return;
		await getAPI(SysDatabaseApi).apiSysDatabaseAddColumnPost(state.ruleForm);
		closeDialog();
	});
};

// 导出对象
defineExpose({ openDialog });
</script>
