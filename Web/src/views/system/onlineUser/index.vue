<template>
	<div class="sys-onlineUser-container">
		<el-drawer v-model="state.isVisible" title="在线用户列表" size="45%">
			<el-card shadow="hover" :body-style="{ paddingBottom: '0' }" style="margin: 8px">
				<el-form :model="state.queryParams" ref="queryForm" :inline="true">
					<el-form-item label="租户" v-if="userStore.userInfos.accountType == 999">
						<el-select v-model="state.queryParams.tenantId" placeholder="租户" style="width: 100%">
							<el-option :value="item.value" :label="`${item.label} (${item.host})`" v-for="(item, index) in state.tenantList" :key="index" />
						</el-select>
					</el-form-item>
					<el-form-item label="账号" prop="userName">
						<el-input placeholder="账号" clearable @keyup.enter="handleQuery" v-model="state.queryParams.userName" />
					</el-form-item>
					<el-form-item label="姓名" prop="realName">
						<el-input placeholder="姓名" clearable @keyup.enter="handleQuery" v-model="state.queryParams.realName" />
					</el-form-item>
					<el-form-item>
						<el-button-group>
							<el-button type="primary" icon="ele-Search" @click="handleQuery"> 查询 </el-button>
							<el-button icon="ele-Refresh" @click="resetQuery"> 重置 </el-button>
						</el-button-group>
					</el-form-item>
				</el-form>
			</el-card>

			<el-card shadow="hover" style="margin: 8px; padding-bottom: 15px">
				<el-table :data="state.onlineUserList" style="width: 100%" v-loading="state.loading" border>
					<el-table-column type="index" label="序号" width="55" align="center" />
					<el-table-column prop="userName" label="账号" header-align="center" show-overflow-tooltip />
					<el-table-column prop="realName" label="姓名" header-align="center" show-overflow-tooltip />
					<el-table-column prop="ip" label="IP地址" min-width="100" header-align="center" show-overflow-tooltip />
					<el-table-column prop="browser" label="浏览器" header-align="center" show-overflow-tooltip />
					<!-- <el-table-column prop="connectionId" label="连接Id" show-overflow-tooltip></el-table-column> -->
					<el-table-column prop="time" label="登录时间" min-width="120" header-align="center" show-overflow-tooltip />
					<el-table-column label="操作" width="81" fixed="right" align="center" show-overflow-tooltip>
						<template #default="scope">
							<el-button icon="ele-CircleCloseFilled" size="small" text type="danger" v-auth="'sysOnlineUser:forceOffline'" @click="forceOffline(scope.row)"> 下线 </el-button>
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
		</el-drawer>
	</div>
</template>

<script lang="ts" setup>
import { onMounted, reactive } from 'vue';
import { ElMessageBox, ElNotification } from 'element-plus';
import { throttle } from 'lodash-es';

import { getAPI, clearAccessAfterReload } from '/@/utils/axios-utils';
import {SysOnlineUserApi, SysAuthApi, SysTenantApi} from '/@/api-services/api';
import { SysOnlineUser } from '/@/api-services/models';
import { useUserInfo } from "/@/stores/userInfo";
import { signalR } from './signalR';

const userStore = useUserInfo();
const state = reactive({
	loading: false,
	isVisible: false,
	tenantList: [] as Array<any>,
	queryParams: {
		userName: undefined,
		realName: undefined,
		tenantId: undefined,
	},
	tableParams: {
		page: 1,
		pageSize: 50,
		total: 1 as any,
	},
	onlineUserList: [] as Array<SysOnlineUser>, // 在线用户列表
	lastUserState: {
		online: false,
		realName: '',
	}, // 最后接收的用户变更状态信息
});

onMounted(async () => {
	if (userStore.userInfos.accountType == 999) {
		state.tenantList = await getAPI(SysTenantApi).apiSysTenantListGet().then(res => res.data.result ?? []);
		state.queryParams.tenantId = userStore.userInfos.currentTenantId as any;
	}
	// 在线用户列表
	signalR.off('OnlineUserList');
	signalR.on('OnlineUserList', (data: any) => {
		state.onlineUserList = data.userList;
		state.lastUserState = {
			online: data.online,
			realName: data.realName,
		};
		notificationThrottle();
	});
	// 强制下线
	signalR.off('ForceOffline');
	signalR.on('ForceOffline', async (data: any) => {
		console.log('强制下线', data);
		await signalR.stop();

		await getAPI(SysAuthApi).apiSysAuthLogoutPost();
		clearAccessAfterReload();
	});
});

// 通知提示节流
const notificationThrottle = throttle(
	function () {
		ElNotification({
			title: '提示',
			message: `${state.lastUserState.online ? `【${state.lastUserState.realName}】上线了` : `【${state.lastUserState.realName}】离开了`}`,
			type: `${state.lastUserState.online ? 'info' : 'error'}`,
			position: 'bottom-right',
		});
	},
	3000,
	{
		leading: true,
		trailing: false,
	}
);

// 打开页面
/**
 * 准备 index 的 open Drawer 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDrawer = () => {
	state.isVisible = true;
	handleQuery();
};

// 查询操作
/**
 * 响应 index 的 handle Query 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleQuery = async () => {
	state.loading = true;
	let params = Object.assign(state.queryParams, state.tableParams);
	var res = await getAPI(SysOnlineUserApi).apiSysOnlineUserPagePost(params);
	state.onlineUserList = res.data.result?.items ?? [];
	state.tableParams.total = res.data.result?.total;
	state.loading = false;
};

// 重置操作
/**
 * 重置或刷新 index 的 reset Query 状态，并重新加载所需数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const resetQuery = () => {
	state.queryParams.userName = undefined;
	state.queryParams.realName = undefined;
	handleQuery();
};

// 强制下线
/**
 * 负责 index 的 force Offline 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const forceOffline = async (row: any) => {
	ElMessageBox.confirm(`确定踢掉账号：【${row.realName}】?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	})
		.then(async () => {
			await signalR.send('ForceOffline', { connectionId: row.connectionId }).catch(function (err: any) {
				console.log(err);
			});
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

// 导出对象
defineExpose({ openDrawer });
</script>
