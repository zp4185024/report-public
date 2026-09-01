<template>
	<div class="sys-region-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="700px">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> {{ props.title }} </span>
				</div>
			</template>
			<el-form :model="state.ruleForm" ref="ruleFormRef" label-width="auto">
				<el-row :gutter="35">
					<!-- <el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="上级名称">
							<el-cascader
								:options="regionData"
								:props="cascaderProps"
								placeholder="请选择上级名称"
								clearable
								class="w100"
								v-model="ruleForm.pid"
							>
								<template #default="{ node, data }">
									<span>{{ data.name }}</span>
									<span v-if="!node.isLeaf"> ({{ data.children.length }}) </span>
								</template>
							</el-cascader>
						</el-form-item>
					</el-col> -->
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="上级行政Id" prop="pid" v-show="false" :rules="[{ required: true, message: '上级行政Id不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.pid" placeholder="上级行政Id或上级行政代码或默认0" clearable />
						</el-form-item>
                        <el-form-item label="上级行政" v-if="!state.ruleForm.id">
                            <!-- <span>{{state.parentNamePath}}</span> -->
                            <el-input v-model="state.parentNamePath" disabled></el-input>
                        </el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="行政名称" prop="name" :rules="[{ required: true, message: '行政名称不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.name" placeholder="行政名称" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="行政代码" prop="code" :rules="[{ required: true, message: '行政代码不能为空', trigger: 'blur' }]">
							<el-input v-model="state.ruleForm.code" placeholder="行政代码" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="区号" prop="cityCode">
							<el-input v-model="state.ruleForm.cityCode" placeholder="区号" clearable />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="排序">
							<el-input-number v-model="state.ruleForm.orderNo" placeholder="排序" class="w100" />
						</el-form-item>
					</el-col>
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl="24" class="mb20">
						<el-form-item label="备注">
							<el-input v-model="state.ruleForm.remark" placeholder="请输入备注内容" clearable type="textarea" />
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

<script lang="ts" setup name="sysEditRegion">
import { reactive, ref } from 'vue';

import { getAPI } from '/@/utils/axios-utils';
import { SysRegionApi } from '/@/api-services/api';
import { UpdateRegionInput } from '/@/api-services/models';

const props = defineProps({
	title: String,
});
const emits = defineEmits(['handleQuery']);
const ruleFormRef = ref();
const state = reactive({
	isShowDialog: false,
	ruleForm: {} as UpdateRegionInput,
    parentNamePath: '顶级'
});

// 打开弹窗
/**
 * 准备 editRegion 的 open Dialog 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @param parentNamePath - parentNamePath 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = (row: any, parentNamePath?: string) => {
    if(parentNamePath) state.parentNamePath = parentNamePath;

	state.ruleForm = JSON.parse(JSON.stringify(row));
	state.isShowDialog = true;
	ruleFormRef.value?.resetFields();
};

// 关闭弹窗
/**
 * 结束 editRegion 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
	emits('handleQuery');
	state.isShowDialog = false;
};

// 取消
/**
 * 校验 editRegion 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

// 提交
/**
 * 校验当前输入并提交保存请求，成功后同步 editRegion 的界面状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const submit = () => {
	ruleFormRef.value.validate(async (valid: boolean) => {
		if (!valid) return;
		if (state.ruleForm.id != undefined && state.ruleForm.id > 0) {
			await getAPI(SysRegionApi).apiSysRegionUpdatePost(state.ruleForm);
		} else {
			await getAPI(SysRegionApi).apiSysRegionAddPost(state.ruleForm);
		}
		closeDialog();
	});
};

// 导出对象
defineExpose({ openDialog });
</script>
