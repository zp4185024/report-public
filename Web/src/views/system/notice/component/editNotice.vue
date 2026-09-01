<template>
	<div class="sys-notice-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="900px">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> {{ props.title }} </span>
				</div>
			</template>
			<el-form :model="state.ruleForm" ref="ruleFormRef" label-width="auto">
				<el-row :gutter="35">
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="标题" prop="title" :rules="[{ required: true, message: '标题不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.title" placeholder="标题" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="12" :md="12" :lg="12" :xl="12" class="mb20">
						<el-form-item label="类型" prop="type" :rules="[{ required: true, message: '类型不能为空', trigger: 'blur' }]">
              <g-sys-dict v-model="state.ruleForm.type" code="NoticeTypeEnum" render-as="select" class="w100" filterable allow-create default-first-option />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="内容" prop="content" :rules="[{ required: true, message: '内容不能为空', trigger: 'blur' }]">
							<Editor v-model:get-html="state.ruleForm.content" />
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

<script lang="ts" setup name="sysNoticeEdit">
import { reactive, ref } from 'vue';
import { getAPI } from '/@/utils/axios-utils';
import { SysNoticeApi } from '/@/api-services/api';
import { UpdateNoticeInput } from '/@/api-services/models';
import Editor from '/@/components/editor/index.vue';

const props = defineProps({
	title: String,
});
const emits = defineEmits(['handleQuery']);
const ruleFormRef = ref();
const state = reactive({
	isShowDialog: false,
	ruleForm: {} as UpdateNoticeInput,
});

// 打开弹窗
/**
 * 准备 editNotice 的 open Dialog 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = (row: any) => {
	state.ruleForm = JSON.parse(JSON.stringify(row));
	state.isShowDialog = true;
	ruleFormRef.value?.resetFields();
};

// 关闭弹窗
/**
 * 结束 editNotice 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
	emits('handleQuery');
	state.isShowDialog = false;
};

// 取消
/**
 * 校验 editNotice 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

// 提交
/**
 * 校验当前输入并提交保存请求，成功后同步 editNotice 的界面状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const submit = () => {
	ruleFormRef.value.validate(async (valid: boolean) => {
		if (!valid) return;
		if (state.ruleForm.id != undefined && state.ruleForm.id > 0) {
			await getAPI(SysNoticeApi).apiSysNoticeUpdatePost(state.ruleForm);
		} else {
			await getAPI(SysNoticeApi).apiSysNoticeAddPost(state.ruleForm);
		}
		closeDialog();
	});
};

// 导出对象
defineExpose({ openDialog });
</script>
