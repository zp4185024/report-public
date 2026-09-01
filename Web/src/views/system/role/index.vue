<template>
	<div class="sys-role-container">
		<el-card shadow="hover" class="card-tight">
			<el-form :model="state.queryParams" ref="queryForm" :inline="true">
				<el-form-item label="租户" v-if="userStore.userInfos.accountType == 999">
					<TenantSelect v-model="state.queryParams.tenantId" clearable />
				</el-form-item>
				<el-form-item label="角色名称">
					<el-input v-model="state.queryParams.name" placeholder="角色名称" clearable />
				</el-form-item>
				<el-form-item label="角色编码">
					<el-input v-model="state.queryParams.code" placeholder="角色编码" clearable />
				</el-form-item>
				<el-form-item>
					<el-button-group>
						<el-button type="primary" icon="ele-Search" @click="handleQuery" v-auth="'sysRole:page'"> 查询 </el-button>
						<el-button icon="ele-Refresh" @click="resetQuery"> 重置 </el-button>
					</el-button-group>
				</el-form-item>
				<el-form-item>
					<el-button type="primary" icon="ele-Plus" @click="openAddRole" v-auth="'sysRole:add'"> 新增 </el-button>
				</el-form-item>
			</el-form>
		</el-card>

		<el-card class="full-table" shadow="hover" style="margin-top: 5px">
			<el-table :data="state.roleData" style="width: 100%" v-loading="state.loading" border>
				<el-table-column type="index" label="序号" width="55" align="center" fixed />
				<el-table-column prop="name" label="角色名称" align="center" show-overflow-tooltip />
				<el-table-column prop="code" label="角色编码" align="center" show-overflow-tooltip />
				<el-table-column label="数据范围" align="center" show-overflow-tooltip>
					<template #default="scope">
            <g-sys-dict v-model="scope.row.dataScope" code="DataScopeEnum" />
					</template>
				</el-table-column>
				<el-table-column prop="orderNo" label="排序" width="70" align="center" show-overflow-tooltip />
				<el-table-column label="状态" width="70" align="center" show-overflow-tooltip>
					<template #default="scope">
						<el-switch v-model="scope.row.status" :active-value="1" :inactive-value="2" size="small" @change="changeStatus(scope.row)" v-auth="'sysRole:setStatus'" />
					</template>
				</el-table-column>
				<el-table-column label="修改记录" width="100" align="center" show-overflow-tooltip>
					<template #default="scope">
						<ModifyRecord :data="scope.row" />
					</template>
				</el-table-column>
				<el-table-column label="操作" width="240" fixed="right" align="center" show-overflow-tooltip>
					<template #default="scope">
						<el-button icon="ele-OfficeBuilding" size="small" text type="primary" @click="openGrantData(scope.row)" v-auth="'sysRole:grantDataScope'"> 数据范围 </el-button>
						<el-button icon="ele-Edit" size="small" text type="primary" @click="openEditRole(scope.row)" v-auth="'sysRole:update'"> 编辑 </el-button>
						<el-button icon="ele-Delete" size="small" text type="danger" @click="delRole(scope.row)" v-auth="'sysRole:delete'"> 删除 </el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-pagination
				v-model:currentPage="state.tableParams.page"
				v-model:page-size="state.tableParams.pageSize"
				:total="state.tableParams.total"
				:page-sizes="[10, 20, 50, 100]"
				size="small"
				background
				@size-change="handleSizeChange"
				@current-change="handleCurrentChange"
				layout="total, sizes, prev, pager, next, jumper"
			/>
		</el-card>

		<EditRole ref="editRoleRef" :title="state.editRoleTitle" @handleQuery="handleQuery" />
		<GrantData ref="grantDataRef" @handleQuery="handleQuery" />
	</div>
</template>

<script lang="ts" setup name="sysRole">
import { onMounted, reactive, ref } from 'vue';
import { ElMessageBox, ElMessage } from 'element-plus';
import EditRole from '/@/views/system/role/component/editRole.vue';
import GrantData from '/@/views/system/role/component/grantData.vue';
import ModifyRecord from '/@/components/table/modifyRecord.vue';
import { getAPI } from '/@/utils/axios-utils';
import { SysRoleApi } from '/@/api-services/api';
import { SysRole } from '/@/api-services/models';
import { useUserInfo } from "/@/stores/userInfo";
import TenantSelect from '/@/views/system/tenant/component/tenantSelect.vue';

const userStore = useUserInfo();
const editRoleRef = ref<InstanceType<typeof EditRole>>();
const grantDataRef = ref<InstanceType<typeof GrantData>>();
const state = reactive({
	loading: false,
	roleData: [] as Array<SysRole>,
	queryParams: {
		tenantId: undefined,
		name: undefined,
		code: undefined,
	},
	tableParams: {
		page: 1,
		pageSize: 50,
		total: 0 as any,
	},
	editRoleTitle: '',
});

onMounted(async () => {
	if (userStore.userInfos.accountType == 999) {
		state.queryParams.tenantId = userStore.userInfos.currentTenantId as any;
	}
	await handleQuery();
});

// 查询操作
/**
 * 响应 index 的 handle Query 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleQuery = async () => {
	state.loading = true;
	let params = Object.assign(state.queryParams, state.tableParams);
	let res = await getAPI(SysRoleApi).apiSysRolePagePost(params);
	state.roleData = res.data.result?.items ?? [];
	state.tableParams.total = res.data.result?.total;
	state.loading = false;
};

// 重置操作
/**
 * 重置或刷新 index 的 reset Query 状态，并重新加载所需数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const resetQuery = async () => {
	state.queryParams.name = undefined;
	state.queryParams.code = undefined;
	await handleQuery();
};

// 打开新增页面
/**
 * 准备 index 的 open Add Role 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openAddRole = () => {
	state.editRoleTitle = '添加角色';
	editRoleRef.value?.openDialog({ id: undefined, status: 1, tenantId: state.queryParams.tenantId, orderNo: 100 });
};

// 打开编辑页面
/**
 * 准备 index 的 open Edit Role 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openEditRole = async (row: any) => {
	state.editRoleTitle = '编辑角色';
	editRoleRef.value?.openDialog(row);
};

// 打开授权数据范围页面
/**
 * 准备 index 的 open Grant Data 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openGrantData = (row: any) => {
	grantDataRef.value?.openDialog(row);
};

// 删除
/**
 * 从 index 移除 del Role 对应数据，并清理相关缓存、选中项或临时状态。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const delRole = (row: any) => {
	ElMessageBox.confirm(`确定删角色：【${row.name}】?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	})
		.then(async () => {
			await getAPI(SysRoleApi).apiSysRoleDeletePost({ id: row.id });
			await handleQuery();
			ElMessage.success('删除成功');
		})
		.catch(() => {});
};

// 改变页面容量
/**
 * 响应 index 的 handle Size Change 事件，并协调相关状态和后续操作。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleSizeChange = async (val: number) => {
	state.tableParams.pageSize = val;
	await handleQuery();
};

// 改变页码序号
/**
 * 响应 index 的 handle Current Change 事件，并协调相关状态和后续操作。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleCurrentChange = async (val: number) => {
	state.tableParams.page = val;
	await handleQuery();
};

// 修改状态
/**
 * 更新 index 的 change Status 状态，并同步相关业务数据。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const changeStatus = async (row: any) => {
	await getAPI(SysRoleApi)
		.apiSysRoleSetStatusPost({ id: row.id, status: row.status })
		.then(() => {
			ElMessage.success('角色状态设置成功');
		})
		.catch(() => {
			row.status = row.status == 1 ? 2 : 1;
		});
};
</script>
