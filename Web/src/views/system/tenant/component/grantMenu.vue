<template>
	<div class="sys-grantMenu-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="769px">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> 授权租户菜单 </span>
				</div>
			</template>
			<el-form :model="state.ruleForm" v-loading="state.loading">
				<el-row :gutter="35">
					<el-col :xs="24" :sm="24" :md="24" :lg="24" :xl1="24">
						<el-form-item prop="orgIdList">
							<el-tree
								ref="treeRef"
								:data="state.menuData"
								node-key="id"
								show-checkbox
								:props="{ children: 'children', label: 'title', class: treeNodeClass }"
								icon="ele-Menu"
								highlight-current
								default-expand-all>
								<template #default="{ node, data }">
									<span :title="node.label+data.id">{{ node.label }}</span>
									<span v-if="data.path" style="margin-left: 5px!important;">
                    <el-tag effect="plain" type="warning" :title="data.component">{{data.path}}</el-tag>
                  </span>
								</template>
							</el-tree>
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

<script lang="ts" setup name="sysGrantMenu">
import { reactive, ref } from 'vue';
import type { ElTree } from 'element-plus';

import { getAPI } from '/@/utils/axios-utils';
import { SysMenuApi, SysTenantApi } from '/@/api-services/api';
import { SysMenu } from '/@/api-services/models';

const treeRef = ref<InstanceType<typeof ElTree>>();
const state = reactive({
	loading: false,
	isShowDialog: false,
	ruleForm: {
		id: 0,
    appId: 0,
		menuIdList: [] as any, // 菜单集合
	},
	menuData: [] as any, // 菜单数据
});

// 打开弹窗
/**
 * 准备 grantMenu 的 open Dialog 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = async (row: any) => {
	treeRef.value?.setCheckedKeys([]); // 先清空已选择节点
	state.ruleForm = row;
  state.menuData = await getAPI(SysMenuApi).apiSysMenuListGet().then(res => res.data.result);
	const menuIds = await getAPI(SysTenantApi).apiSysTenantTenantMenuListGet(row.id).then(res => res.data.result);
	setTimeout(() => {
		// 延迟传递数据
		treeRef.value?.setCheckedKeys(menuIds ?? []);
	}, 100);
	state.isShowDialog = true;
};

// 取消
/**
 * 校验 grantMenu 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

// 提交
/**
 * 校验当前输入并提交保存请求，成功后同步 grantMenu 的界面状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const submit = async () => {
	state.ruleForm.menuIdList = treeRef.value?.getCheckedKeys() as Array<number>;
	await getAPI(SysTenantApi).apiSysTenantGrantMenuPost(state.ruleForm);
	state.isShowDialog = false;
};

// 叶子节点同行显示样式
/**
 * 负责 grantMenu 的 tree Node Class 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const treeNodeClass = (node: SysMenu) => {
	let addClass = true; // 添加叶子节点同行显示样式
	for (const key in node.children) {
		// 如果存在子节点非叶子节点，不添加样式
		if (node.children[key].children?.length ?? 0 > 0) {
			addClass = false;
			break;
		}
	}
	return addClass ? 'penultimate-node' : '';
};

// 导出对象
defineExpose({ openDialog });
</script>

<style lang="scss" scoped>
:deep(.penultimate-node) {
	.el-tree-node__children {
		padding-left: 40px;
		white-space: pre-wrap;
		line-height: 100%;

		.el-tree-node {
			display: inline-block;
		}

		.el-tree-node__content {
			padding-left: 5px !important;
			padding-right: 5px;

			// .el-tree-node__expand-icon {
			// 	display: none;
			// }
		}
	}
}
</style>
