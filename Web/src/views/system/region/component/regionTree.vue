<template>
	<el-card class="box-card" shadow="hover" style="height: 100%" body-style="height:calc(100% - 65px); overflow:auto">
		<template #header>
			<div class="card-header">
				<div class="tree-h-flex">
					<div class="tree-h-left">
						<el-input :prefix-icon="Search" v-model="filterText" placeholder="行政区域名称" />
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
		<div v-loading="state.loading">
			<el-tree
				ref="treeRef"
				class="filter-tree"
				:data="state.regionData"
				node-key="id"
				:props="{ children: 'children', label: 'name' }"
				:filter-node-method="filterNode"
				@node-click="nodeClick"
				highlight-current
				check-strictly
				accordion
				lazy
				:load="loadNode"
			/>
		</div>
	</el-card>
</template>

<script lang="ts" setup>
import { onMounted, reactive, ref, watch } from 'vue';
import type { ElTree } from 'element-plus';
import { Search, MoreFilled } from '@element-plus/icons-vue';

import { getAPI } from '/@/utils/axios-utils';
import { SysRegionApi } from '/@/api-services/api';
import { SysRegion } from '/@/api-services/models';

const filterText = ref('');
const treeRef = ref<InstanceType<typeof ElTree>>();
const state = reactive({
	loading: false,
	regionData: [] as Array<SysRegion>,
});

onMounted(() => {
	initTreeData();
});

watch(filterText, (val) => {
	treeRef.value!.filter(val);
});

/**
 * 初始化 regionTree 的 init Tree Data 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initTreeData = async () => {
	state.loading = true;
	var res = await getAPI(SysRegionApi).apiSysRegionListGet(0);
	state.regionData = res.data.result ?? [];
	state.loading = false;
};

/**
 * 查询或读取 regionTree 所需的 load Node 数据，并按调用约定返回结果。
 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param resolve - resolve 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const loadNode = async (node: any, resolve: any) => {
	if (node.data == undefined || Array.isArray(node.data)) return;

	state.loading = true;
	var res = await getAPI(SysRegionApi).apiSysRegionListGet(node.data.id);
	var data = res.data.result ?? [];
	state.loading = false;
	resolve(data);
};

/**
 * 获取选中节点的Keys
 * @returns 选中节点的Key数组
 */
const getCheckedKeys = () => {
    return treeRef.value!.getCheckedKeys();
};

/**
 * 获取当前选中节点
 * @returns 当前选中节点
 */
const getCurrentNode = () => {
    return treeRef.value!.getCurrentNode();
};

/**
 * 获取当前选中节点的路径数组
 * （从根节点到当前节点，按下标顺序排列）
 * @returns {Array<{ id: number, name: string }>} 路径数组
 */
const getCurrentPath = () => {
    const currentNode = getCurrentNode();
    if(!currentNode) return null;

    const cascaderData = getCascaderData(currentNode);
    const path = [] as Array<{ id: number, name: string }>;
    let node = cascaderData;
    while(node) {
        path.push({ id: node.id, name: node.name });
        node = node.child;
    }
    return path;
};

// 递归获取当前选中级联数据
/**
 * 查询或读取 regionTree 所需的 get Cascader Data 数据，并按调用约定返回结果。
 * @param child - child 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const getCascaderData = (child: any) => {
    const parent = treeRef.value!.getNode(child.pid)?.data as SysRegion & { child?: SysRegion };
    if(!parent) return child;

    parent.child = child;
    if(parent.pid != 0) {
        return getCascaderData(parent);
    }

    return parent;
};

/**
 * 按 filter Node 条件比较、筛选或整理 regionTree 数据，并返回顺序稳定的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @param data - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const filterNode = (value: string, data: any) => {
	if (!value) return true;
	return data.name.includes(value);
};

/**
 * 响应 regionTree 的 handle Command 事件，并协调相关状态和后续操作。
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
 * 负责 regionTree 的 node Click 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const nodeClick = (node: any) => {
	emits('node-click', { id: node.id, name: node.name });
};

// 导出对象
defineExpose({ initTreeData, getCheckedKeys, getCurrentNode, getCurrentPath });
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
