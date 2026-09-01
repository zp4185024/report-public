<template>
	<el-card class="box-card" shadow="hover" style="height: 100%" body-style="height:100%; overflow:auto">
		<template #header>
			<div class="card-header">
				<div class="tree-h-flex" v-if="!props.tenantId">
					<TenantSelect v-model="state.tenantId" @change="initTreeData()" clearable class="mb10" />
				</div>
				<div class="tree-h-flex">
					<div class="tree-h-left">
						<el-input :prefix-icon="Search" v-model="filterText" placeholder="机构名称" />
					</div>
					<div class="tree-h-right">
						<el-dropdown @command="handleCommand">
							<el-button style="margin-left: 8px; width: 34px">
								<el-icon class="el-icon--center">
									<more-filled />
								</el-icon>
							</el-button>
							<template #dropdown>
								<el-dropdown-menu>
									<el-dropdown-item command="expandAll">全部展开</el-dropdown-item>
									<el-dropdown-item command="collapseAll">全部折叠</el-dropdown-item>
									<el-dropdown-item command="rootNode">根节点</el-dropdown-item>
									<el-dropdown-item command="refresh">刷新</el-dropdown-item>
								</el-dropdown-menu>
							</template>
						</el-dropdown>
					</div>
				</div>
			</div>
		</template>
		<div style="margin-bottom: 45px" v-loading="state.loading">
			<el-tree
				ref="treeRef"
				class="filter-tree"
				:data="state.orgData"
				node-key="id"
				:props="{ children: 'children', label: 'name' }"
				:filter-node-method="filterNode"
				@node-click="nodeClick"
				:show-checkbox="state.isShowCheckbox"
				:default-checked-keys="state.ownOrgData"
				highlight-current
				check-strictly
			>
				<template #default="{ node }">
					<el-icon v-if="node.level == 1" size="16" style="margin-right: 3px; display: inline; vertical-align: middle"><ele-School /></el-icon>
					<el-icon v-else-if="node.level == 2" size="16" style="margin-right: 3px; display: inline; vertical-align: middle"><ele-PriceTag /></el-icon>
					<el-icon v-else size="16" style="margin-right: 3px; display: inline; vertical-align: middle"><ele-CollectionTag /></el-icon>
					{{ node.label }}
				</template>
			</el-tree>
		</div>
	</el-card>
</template>

<script lang="ts" setup>
import { onMounted, reactive, ref, watch } from 'vue';
import type { ElTree } from 'element-plus';
import { Search, MoreFilled } from '@element-plus/icons-vue';

import { getAPI } from '/@/utils/axios-utils';
import { SysOrgApi } from '/@/api-services/api';
import { OrgTreeOutput } from '/@/api-services/models';
import TenantSelect from '/@/views/system/tenant/component/tenantSelect.vue';

const props = defineProps({
	tenantId: [Number, String],
});
const filterText = ref('');
const treeRef = ref<InstanceType<typeof ElTree>>();
const state = reactive({
	loading: false,
	tenantId: props.tenantId as number,
	orgData: [] as Array<OrgTreeOutput>,
	isShowCheckbox: false,
	ownOrgData: [] as Array<OrgTreeOutput>,
});

onMounted( async () => {
	initTreeData();
});

watch(filterText, (val) => {
	treeRef.value!.filter(val);
});

/**
 * 初始化 orgTree 的 init Tree Data 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initTreeData = async () => {
	state.loading = true;
	const res = await getAPI(SysOrgApi).apiSysOrgTreeGet(0, undefined, undefined, undefined, state.tenantId);
	state.orgData = res.data.result ?? [];
	state.loading = false;
};

// 设置默认选择
/**
 * 更新 orgTree 的 set Checked Keys 状态，并同步相关业务数据。
 * @param data - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const setCheckedKeys = (data: any) => {
	const isArray = Array.isArray(data);
	treeRef.value!.setCheckedKeys([]);
	if (!isArray) {
		treeRef.value!.setCurrentNode(data);
		nodeClick(data);
	}
	state.ownOrgData = isArray ? data : [data];
	state.isShowCheckbox = isArray;
};

// 获取已经选择
/**
 * 查询或读取 orgTree 所需的 get Checked Keys 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const getCheckedKeys = () => {
	return treeRef.value!.getCheckedKeys();
};

/**
 * 按 filter Node 条件比较、筛选或整理 orgTree 数据，并返回顺序稳定的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @param data - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const filterNode = (value: string, data: any) => {
	if (!value) return true;
	return data.name.includes(value);
};

/**
 * 响应 orgTree 的 handle Command 事件，并协调相关状态和后续操作。
 * @param command - command 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleCommand = async (command: string | number | object) => {
	if ('expandAll' == command) {
		for (let i = 0; i < treeRef.value!.store._getAllNodes().length; i++) {
			treeRef.value!.store._getAllNodes()[i].expanded = true;
		}
	} else if ('collapseAll' == command) {
		for (let i = 0; i < treeRef.value!.store._getAllNodes().length; i++) {
			treeRef.value!.store._getAllNodes()[i].expanded = false;
		}
	} else if ('refresh' == command) {
		initTreeData();
	} else if ('rootNode' == command) {
		emits('node-click', { id: 0, name: '' });
	}
};

// 与父组件的交互逻辑
const emits = defineEmits(['node-click']);
/**
 * 负责 orgTree 的 node Click 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const nodeClick = (node: any) => {
	emits('node-click', node);
};

// 导出对象
defineExpose({ initTreeData, setCheckedKeys, getCheckedKeys });
</script>

<style lang="scss" scoped>
.tree-h-flex {
	display: flex;
}

.tree-h-left {
	flex: 1;
	width: 100%;
}

.tree-h-right {
	width: 42px;
	min-width: 42px;
}
</style>
