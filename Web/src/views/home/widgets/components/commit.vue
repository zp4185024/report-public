<template>
	<card-pro title="更新记录" prefix-icon="ele-DocumentCopy" shadow="hover">
        <div class="commit" v-loading="state.loading">
			<el-timeline style="max-width: 600px" v-if="state.list.length > 0">
				<el-timeline-item v-for="(item, index) in state.list" :key="index" :timestamp="formatDate(new Date(item.commit.committer.date), 'YYYY-mm-dd HH:MM:SS')">
					<el-link style="white-space: pre-line; word-break: break-all" :href="item.html_url" target="_blank"> {{ item.commit.message }}</el-link>
				</el-timeline-item>
			</el-timeline>
			<el-empty v-else description="空"></el-empty>
		</div>
        <template #suffix>
            <el-button type="primary" icon="ele-Refresh" round plain @click="refresh">更新记录</el-button>
        </template>
    </card-pro>
</template>

<script lang="ts">
export default {
	title: '更新记录',
	icon: 'ele-DocumentCopy',
	description: '当前项目更新记录',
};
</script>

<script setup lang="ts" name="commit">
import { reactive, onMounted } from 'vue';
import { formatDate } from '/@/utils/formatTime';
import CardPro from '/@/components/CardPro/index.vue';

const state = reactive({
	loading: false,
	list: [] as any,
});

/**
 * 查询或读取 commit 所需的 get List 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const getList = () => {
	axios({
		method: 'get',
		url: 'https://gitee.com/api/v5/repos/zuohuaijun/Admin.NET/commits',
		params: {
			page: 1,
			per_page: 10,
		},
	}).then((res: any) => {
		state.list = res.data;
		state.loading = false;
	});
};

/**
 * 重置或刷新 commit 的 refresh 状态，并重新加载所需数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const refresh = () => {
	state.loading = true;
	getList();
};

onMounted(() => {
	state.loading = true;
	getList();
});
</script>

<style scoped>
.progress {
	text-align: center;
}
.progress .percentage-value {
	font-size: 28px;
}
.progress .percentage-label {
	font-size: 12px;
	margin-top: 10px;
}
.commit {
	max-height: 500px;
	overflow: auto;
}
</style>
