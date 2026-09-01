<template>
	<div class="sys-print-container">
		<el-card shadow="hover" class="card-tight">
			<el-form :model="state.queryParams" ref="queryForm" :inline="true">
				<el-form-item label="租户" v-if="userStore.userInfos.accountType == 999">
                    <TenantSelect v-model="state.queryParams.tenantId" clearable />
				</el-form-item>
				<el-form-item label="模板名称">
					<el-input v-model="state.queryParams.name" placeholder="模板名称" clearable />
				</el-form-item>
				<el-form-item>
					<el-button-group>
						<el-button type="primary" icon="ele-Search" @click="handleQuery" v-auth="'sysPrint:page'"> 查询 </el-button>
						<el-button icon="ele-Refresh" @click="resetQuery"> 重置 </el-button>
					</el-button-group>
				</el-form-item>
				<el-form-item>
					<el-button type="primary" icon="ele-Plus" @click="openAddPrint" v-auth="'sysPrint:add'"> 新增 </el-button>
				</el-form-item>
			</el-form>
		</el-card>

		<el-card class="full-table" shadow="hover" style="margin-top: 5px">
			<el-table :data="state.printData" style="width: 100%" v-loading="state.loading" border>
				<el-table-column type="index" label="序号" width="55" align="center" fixed />
				<el-table-column prop="name" label="名称" header-align="center" show-overflow-tooltip />
				<!-- <el-table-column prop="template" label="模板" show-overflow-tooltip /> -->
				<el-table-column prop="orderNo" label="排序" align="center" show-overflow-tooltip />
				<el-table-column label="状态" align="center" show-overflow-tooltip>
					<template #default="scope">
            <g-sys-dict v-model="scope.row.status" code="StatusEnum" />
					</template>
				</el-table-column>
				<el-table-column label="修改记录" width="100" align="center" show-overflow-tooltip>
					<template #default="scope">
						<ModifyRecord :data="scope.row" />
					</template>
				</el-table-column>
				<el-table-column label="操作" width="140" fixed="right" align="center" show-overflow-tooltip>
					<template #default="scope">
						<el-button icon="ele-Edit" size="small" text type="primary" @click="openEditPrint(scope.row)" v-auth="'sysPrint:update'"> 编辑 </el-button>
						<el-button icon="ele-Delete" size="small" text type="danger" @click="delPrint(scope.row)" v-auth="'sysPrint:delete'"> 删除 </el-button>
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

		<EditPrint ref="editPrintRef" :title="state.editPrintTitle" @handleQuery="handleQuery" />
	</div>
</template>

<script lang="ts" setup name="sysPrint">
import { onMounted, reactive, ref } from 'vue';
import { ElMessageBox, ElMessage } from 'element-plus';
import EditPrint from '/@/views/system/print/component/editPrint.vue';
import ModifyRecord from '/@/components/table/modifyRecord.vue';
import { getAPI } from '/@/utils/axios-utils';
import { SysPrintApi } from '/@/api-services/api';
import { SysPrint } from '/@/api-services/models';
import { useUserInfo } from "/@/stores/userInfo";
import TenantSelect from '/@/views/system/tenant/component/tenantSelect.vue';

const userStore = useUserInfo();
const editPrintRef = ref<InstanceType<typeof EditPrint>>();
const state = reactive({
	loading: false,
	printData: [] as Array<SysPrint>,
	queryParams: {
		tenantId: undefined,
		name: undefined,
	},
	tableParams: {
		page: 1,
		pageSize: 50,
		total: 0 as any,
	},
	editPrintTitle: '',
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
	var res = await getAPI(SysPrintApi).apiSysPrintPagePost(params);
	state.printData = res.data.result?.items ?? [];
	state.tableParams.total = res.data.result?.total;
	state.loading = false;
};

// 重置操作
/**
 * 重置或刷新 index 的 reset Query 状态，并重新加载所需数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const resetQuery = () => {
	state.queryParams.name = undefined;
	handleQuery();
};

// 打开新增页面
/**
 * 准备 index 的 open Add Print 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openAddPrint = () => {
	state.editPrintTitle = '添加打印模板';
	editPrintRef.value?.openDialog({ tenantId: state.queryParams.tenantId });
};

// 打开编辑页面
/**
 * 准备 index 的 open Edit Print 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openEditPrint = (row: any) => {
	state.editPrintTitle = '编辑打印模板';
	editPrintRef.value?.openDialog(row);
};

// 删除当前行
/**
 * 从 index 移除 del Print 对应数据，并清理相关缓存、选中项或临时状态。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const delPrint = (row: any) => {
	ElMessageBox.confirm(`确定删除打印模板：【${row.name}】?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	})
		.then(async () => {
			await getAPI(SysPrintApi).apiSysPrintDeletePost({ id: row.id });
			handleQuery();
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
</script>
