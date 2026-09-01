<template>
	<div class="sysLdap-container">
		<el-card shadow="hover" class="card-tight">
			<el-form :model="state.queryParams" ref="queryForm" :inline="true">
				<el-form-item label="租户" v-if="userStore.userInfos.accountType == 999">
					<TenantSelect v-model="state.queryParams.tenantId" clearable />
				</el-form-item>
				<el-form-item label="关键字">
					<el-input v-model="state.queryParams.keyword" clearable placeholder="请输入模糊查询关键字" />
				</el-form-item>
				<el-form-item label="主机">
					<el-input v-model="state.queryParams.host" clearable placeholder="请输入主机" />
				</el-form-item>
				<el-form-item>
					<el-button-group>
						<el-button type="primary" icon="ele-Search" @click="handleQuery" v-auth="'sysLdap:page'"> 查询 </el-button>
						<el-button icon="ele-Refresh" @click="resetQuery"> 重置 </el-button>
					</el-button-group>
				</el-form-item>
				<el-form-item>
					<el-button type="primary" icon="ele-Plus" @click="openAddSysLdap" v-auth="'sysLdap:add'"> 新增 </el-button>
				</el-form-item>
			</el-form>
		</el-card>

		<el-card class="full-table" shadow="hover" style="margin-top: 5px">
			<el-table :data="state.tableData" style="width: 100%" v-loading="state.loading" border>
				<el-table-column type="index" label="序号" width="55" align="center" />
				<el-table-column prop="host" label="主机" min-width="150" show-overflow-tooltip />
				<el-table-column prop="port" label="端口" show-overflow-tooltip />
				<el-table-column prop="baseDn" label="用户搜索基准" show-overflow-tooltip />
				<el-table-column prop="bindDn" label="绑定DN" show-overflow-tooltip />
				<el-table-column prop="bindPass" label="绑定密码" min-width="200" show-overflow-tooltip />
				<el-table-column prop="authFilter" label="用户过滤规则" show-overflow-tooltip />
				<el-table-column prop="version" label="Ldap版本" show-overflow-tooltip />
				<el-table-column prop="status" label="状态" width="80" align="center" show-overflow-tooltip>
					<template #default="scope">
            <g-sys-dict v-model="scope.row.status" code="StatusEnum" />
					</template>
				</el-table-column>
				<el-table-column label="修改记录" width="100" align="center" show-overflow-tooltip>
					<template #default="scope">
						<ModifyRecord :data="scope.row" />
					</template>
				</el-table-column>
				<el-table-column label="操作" width="300" align="center" fixed="right" show-overflow-tooltip v-if="auth('sysLdap:update') || auth('sysLdap:delete') || auth('sysLdap:syncUser') || auth('sysLdap:syncOrg')">
					<template #default="scope">
						<el-button icon="ele-Edit" size="small" text type="primary" @click="openEditSysLdap(scope.row)" v-auth="'sysLdap:update'"> 编辑 </el-button>
						<el-button icon="ele-Delete" size="small" text type="danger" @click="delSysLdap(scope.row)" v-auth="'sysLdap:delete'"> 删除 </el-button>
						<el-button icon="ele-Refresh" size="small" text type="primary" @click="syncDomainUser(scope.row)" v-auth="'sysLdap:syncUser'"> 同步域账户 </el-button>
						<el-button icon="ele-Refresh" size="small" text type="primary" @click="syncDomainOrg(scope.row)" v-auth="'sysLdap:syncOrg'"> 同步域组织 </el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-pagination
				v-model:currentPage="state.tableParams.page"
				v-model:page-size="state.tableParams.pageSize"
				:total="state.tableParams.total"
				:page-sizes="[10, 20, 50, 100, 200, 500]"
				size="small"
				background
				@size-change="handleSizeChange"
				@current-change="handleCurrentChange"
				layout="total, sizes, prev, pager, next, jumper"
			/>
		</el-card>

		<EditLdap ref="editLdapRef" :title="state.dialogTitle" @handleQuery="handleQuery" />
	</div>
</template>

<script lang="ts" setup name="sysLdap">
import { onMounted, reactive, ref } from 'vue';
import { ElMessageBox, ElMessage } from 'element-plus';
import { auth } from '/@/utils/authFunction';
import { getAPI } from '/@/utils/axios-utils';
import { SysLdapApi } from '/@/api-services/api';
import ModifyRecord from '/@/components/table/modifyRecord.vue';
import EditLdap from './component/editLdap.vue';
import { useUserInfo } from "/@/stores/userInfo";
import TenantSelect from '/@/views/system/tenant/component/tenantSelect.vue';

const userStore = useUserInfo();
const editLdapRef = ref<InstanceType<typeof EditLdap>>();
const state = reactive({
	loading: false,
	tableData: [] as any,
	queryParams: {
		tenantId: undefined,
		keyword: undefined,
		host: undefined,
	},
	tableParams: {
		page: 1,
		pageSize: 50,
		total: 0 as any,
	},
	dialogTitle: '',
});

onMounted(async () => {
	if (userStore.userInfos.accountType == 999) {
		state.queryParams.tenantId = userStore.userInfos.currentTenantId as any;
	}
	handleQuery();
});

// 查询操作
/**
 * 响应 index 的 handle Query 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleQuery = async () => {
	state.loading = true;
	let params = Object.assign(state.queryParams, state.tableParams);
	var res = await getAPI(SysLdapApi).apiSysLdapPagePost(params);
	state.tableData = res.data.result?.items ?? [];
	state.tableParams.total = res.data.result?.total;
	state.loading = false;
};

// 重置操作
/**
 * 重置或刷新 index 的 reset Query 状态，并重新加载所需数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const resetQuery = () => {
	state.queryParams.keyword = undefined;
	state.queryParams.host = undefined;
	handleQuery();
};

// 打开新增页面
/**
 * 准备 index 的 open Add Sys Ldap 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openAddSysLdap = () => {
	state.dialogTitle = '添加系统域登录信息配置';
	editLdapRef.value?.openDialog({ tenantId: state.queryParams.tenantId });
};

// 打开编辑页面
/**
 * 准备 index 的 open Edit Sys Ldap 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openEditSysLdap = (row: any) => {
	state.dialogTitle = '编辑系统域登录信息配置';
	editLdapRef.value?.openDialog(row);
};

// 删除
/**
 * 从 index 移除 del Sys Ldap 对应数据，并清理相关缓存、选中项或临时状态。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const delSysLdap = (row: any) => {
	ElMessageBox.confirm(`确定要删除域登录信息配置：【${row.host}】?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	}).then(async () => {
		await getAPI(SysLdapApi).apiSysLdapDeletePost({ id: row.id });
		handleQuery();
		ElMessage.success('删除成功');
	}).catch(() => {});
};

// 改变页面容量
/**
 * 响应 index 的 handle Size Change 事件，并协调相关状态和后续操作。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleSizeChange = (val: number) => {
	state.tableParams.pageSize = val;
	handleQuery();
};

// 改变页码序号
/**
 * 响应 index 的 handle Current Change 事件，并协调相关状态和后续操作。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleCurrentChange = (val: number) => {
	state.tableParams.page = val;
	handleQuery();
};

// 同步域账户
/**
 * 更新 index 的 sync Domain User 状态，并把变化同步到关联组件或业务数据。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const syncDomainUser = (row: any) => {
	ElMessageBox.confirm(`确定要同步域账户吗?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	}).then(async () => {
		await getAPI(SysLdapApi).apiSysLdapSyncUserPost({ id: row.id });
		handleQuery();
		ElMessage.success('删除成功');
	}).catch(() => {});
};

// 同步域组织
/**
 * 更新 index 的 sync Domain Org 状态，并把变化同步到关联组件或业务数据。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const syncDomainOrg = (row: any) => {
	ElMessageBox.confirm(`确定要同步域组织架构吗?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	}).then(async () => {
		await getAPI(SysLdapApi).apiSysLdapSyncOrgPost({ id: row.id });
		handleQuery();
		ElMessage.success('删除成功');
	}).catch(() => {});
};
</script>
