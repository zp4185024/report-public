<template>
	<input class="el-upload__input" ref="reffile" name="file" @change="fileChange($event)" :accept="$props.accept" type="file" />
	<el-button icon="ele-Upload" @click="onClick">
		<slot></slot>
	</el-button>
</template>

<script setup lang="ts" name="ImportButton">
import { reactive, ref, onMounted, watch } from 'vue';
import { request2 } from '/@/utils/request';
import { ElMessage } from 'element-plus';

// 定义父组件传过来的值
const props = defineProps({
	accept: {
		type: String,
	},
	param: {
		type: Object,
		/**
		 * 为 param 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => {},
	},
	url: {
		type: String,
	},
});

// 定义子组件向父组件传值/事件
const emit = defineEmits(['success', 'error']);
const reffile = ref();
const state = reactive({
	form: {} as any,
});

watch(
	() => props.param,
	(value) => {
		if (value) {
			state.form = Object.assign({}, { ...value });
		}
	},
	{
		immediate: true,
		deep: true,
	}
);

// 上传文件
/**
 * 响应 index 的 on Click 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onClick = () => {
	reffile.value.click();
};

/**
 * 校验 index 的 file Change 文件输入，读取内容并更新目标数据或预览状态。
 * @param event - 触发当前处理流程的界面或业务事件。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function fileChange(event: any) {
	if (!event.currentTarget.files || event.currentTarget.files.length == 0) {
		return;
	}
	var file = event.currentTarget.files[0];
	let formData = new FormData();
	formData.append('file', file);
	for (const key in state.form) {
		const element = state.form[key];
		formData.append(key, element);
	}
	request2({
		url: props.url,
		method: 'post',
		data: formData,
		headers: {
			'Content-Type': 'multipart/form-data',
		},
	})
		.then((res: any) => {
			ElMessage.success(res);
			reffile.value.files = null;
			emit('success', res);
		})
		.catch((res: any) => {
			alert('上传错误');
		});
}
</script>
