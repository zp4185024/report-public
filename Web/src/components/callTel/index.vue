<template>
	<div class="page-container">
		<!-- 姓名和号码部分 -->
		<div class="name-number" v-if="state.data">
			<span class="name">{{ state.data.realName }}</span>
			<span class="spacing"></span> <!-- 新增的间距元素 -->
			<span class="number">{{ state.data.number }}</span>
		</div>
		<!-- 头像容器 -->
		<div class="avatars">
			<!-- 绿色头像 -->
			<div class="avatar-wrap green-icon-wrap" @click="handleClick(true)" v-reclick="1000">
				<el-avatar :size="100" class="icon green-icon">
					<i class="iconfont icon-dianhua"></i>
				</el-avatar>
			</div>
		</div>
	</div>
</template>

<script lang="ts" name="callTel" setup>
import { reactive, onMounted } from "vue";
import { useRoute } from "vue-router";
const route = useRoute();

const state = reactive({
	token: null as any,
	data: {} as any
});

// 页面初始化
/**
 * 初始化 index 的 initialize Page 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initializePage = () => {
	state.data = JSON.parse(decodeURIComponent(route.query.code || '{}' as any));
	state.token = route.query.token;
};

onMounted(() => {
	initializePage();
});

// 点击事件
/**
 * 响应 index 的 handle Click 事件，并协调相关状态和后续操作。
 * @param success - success 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleClick = (success: boolean) => {
	if (success) location.href = 'tel:' + state.data.number;
};
</script>

<style lang="scss" scoped>
/* 样式保持不变 */
.page-container {
	display: flex;
	flex-direction: column;
	justify-content: flex-start; /* 使用flex-start而不是center以控制垂直对齐 */
	align-items: center; /* 水平居中 */
	height: 100vh;
	padding-top: calc(100vh * (1 - 1/1.618)); /* 使用黄金分割比例计算顶部内边距 */
}

.name-number {
	text-align: center;
	margin-bottom: 20px; /* 添加与头像之间的间距 */
	.name {
		font-weight: bold;
		font-size: 24px; /* 增大字体大小 */
	}

	.spacing {
		display: block;
		height: 10px; /* 设置间距的高度 */
	}

	.number {
		font-size: 20px; /* 增大字体大小 */
		color: #a09e9e; /* 更改号码颜色为指定的浅灰色 */
		font-weight: 600; /* 加粗字体 */
	}
}

.avatars {
	display: flex;
	justify-content: center; /* 使子元素（绿色头像）水平居中 */
	gap: 40px; /* 增加头像之间的间距 */
	.avatar-wrap {
		cursor: pointer;
		transition: background-color 0.2s, transform 0.2s;
	}

	.avatar-wrap:hover .icon {
		filter: brightness(90%); /* 鼠标悬停时稍微变暗 */
	}

	.avatar-wrap:active .icon {
		filter: brightness(80%); /* 点击时更明显地变暗 */
		transform: scale(0.94); /* 点击时轻微缩小 */
	}

	.icon {
		color: white;
		transition: filter 0.2s, transform 0.2s;
	}

	.iconfont {
		font-size: 70px;
	}

	.green-icon {
		background-color: lawngreen;
	}

	.red-icon {
		background-color: red;
	}

	.green-icon-wrap .icon {
		background-color: lawngreen;
	}

	.red-icon-wrap .icon {
		background-color: red;
	}
}
</style>