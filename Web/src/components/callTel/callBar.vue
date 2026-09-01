<script lang="ts" setup>
import { nextTick, onMounted, ref } from "vue";
import QRCode from 'qrcodejs2-fixes';

// 组件属性
const props = defineProps({
	realName: String,
	number: String,
	callbackUrl: String,
	callbackData: String,
  needToken: Boolean,
});

// 定义变量内容
const qrcodeRef = ref<HTMLElement | null>(null);

// 初始化生成二维码
/**
 * 初始化 callBar 的 init Qrcode 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initQrcode = () => {
	nextTick(() => {
		const token = props.needToken ? Local.get(accessTokenKey) : '';
		const code = encodeURIComponent(JSON.stringify({
			realName: props.realName,
			number: props.number,
			callbackUrl: props.callbackUrl,
			callbackData: props.callbackData
		}));
		let url = `${location.origin}/$callTel#/$callTel?code=${encodeURIComponent(code)}&token=${encodeURIComponent(token)}`;
		(<HTMLElement>qrcodeRef.value).innerHTML = '';
		new QRCode(qrcodeRef.value, {
			text: url,
			width: 260,
			height: 260,
			colorDark: '#000000',
			colorLight: '#ffffff',
		});
	});
};

// 拨打电话
/**
 * 校验 callBar 的 call Tel 输入后执行核心操作，并把成功或失败结果交给调用方。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const callTel = () => {
	location.href = 'tel:' + props.number;
}

// 页面加载时
onMounted(() => {
	initQrcode();
});
</script>

<template>
	<el-popover placement="bottom" width="300" trigger="click">
		<template #reference>
			<i class="iconfont icon-dianhua" v-bind="$attrs" />
		</template>
		<el-descriptions direction="vertical" :column="1" border>
			<el-descriptions-item align="center">
				<template #label>
					<el-button @click="callTel">直接拨打</el-button>
				</template>
			</el-descriptions-item>
			<el-descriptions-item width="140" align="center">
				手机扫一扫
				<template #label>
					<div ref="qrcodeRef" />
				</template>
			</el-descriptions-item>
		</el-descriptions>
	</el-popover>
</template>

<style scoped lang="scss">
.call-bar-container {
	display: flex;
}
</style>