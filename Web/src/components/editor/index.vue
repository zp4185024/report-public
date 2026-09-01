<template>
	<div class="editor-container">
		<Toolbar :editor="editorRef" :mode="mode" />
		<Editor :mode="mode" :defaultConfig="state.editorConfig" :style="{ height }" v-model="state.editorVal" @onCreated="handleCreated" @onChange="handleChange" />
	</div>
</template>

<script setup lang="ts" name="wngEditor">
// https://www.wangeditor.com/v5/for-frame.html#vue3
import '@wangeditor-next/editor/dist/css/style.css';
import { reactive, shallowRef, watch, onBeforeUnmount } from 'vue';
import { IDomEditor } from '@wangeditor-next/editor';
import { Toolbar, Editor } from '@wangeditor-next/editor-for-vue';
import { ElMessage } from 'element-plus';
import { getAPI } from '/@/utils/axios-utils';
import { SysFileApi } from '/@/api-services/api';

// 定义父组件传过来的值
const props = defineProps({
	// 是否禁用
	disable: {
		type: Boolean,
		/**
		 * 为 disable 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => false,
	},
	// 内容框默认 placeholder
	placeholder: {
		type: String,
		/**
		 * 为 placeholder 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '请输入内容...',
	},
	// https://www.wangeditor.com/v5/getting-started.html#mode-%E6%A8%A1%E5%BC%8F
	// 模式，可选 <default|simple>，默认 default
	mode: {
		type: String,
		/**
		 * 为 mode 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => 'default',
	},
	// 高度
	height: {
		type: String,
		/**
		 * 为 height 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '310px',
	},
	// 双向绑定，用于获取 editor.getHtml()
	getHtml: String,
	// 双向绑定，用于获取 editor.getText()
	getText: String,
});

// 定义子组件向父组件传值/事件
const emit = defineEmits(['update:getHtml', 'update:getText']);

// 定义变量内容
const editorRef = shallowRef();
const state = reactive({
	editorConfig: {
		placeholder: props.placeholder,
		// 菜单配置
		MENU_CONF: {
			uploadImage: {
				fieldName: 'file',
				/**
				 * 负责 index 的 custom Upload 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
				 * @param file - file 对应的业务输入；具体类型和空值范围以方法签名为准。
				 * @param insertFn - insertFn 对应的业务输入；具体类型和空值范围以方法签名为准。
				 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
				 */
				customUpload(file: File, insertFn: any) {
					getAPI(SysFileApi).apiSysFileUploadFilePostForm(file).then(({data}) => {
						if (data.type == 'success' && data.result) {
							editorRef.value.insertNode({ type: 'image', src: data.result.url, alt: data.result.fileName, href: data.result.url, children: [{ text: '' }] })
						} else {
							ElMessage.error('上传失败！')
						}
					})
				},
			},
			insertImage: {
				/**
				 * 校验 index 的 check Image 条件，并返回可供后续流程判断的结果。
				 * @param src - src 对应的业务输入；具体类型和空值范围以方法签名为准。
				 * @param alt - alt 对应的业务输入；具体类型和空值范围以方法签名为准。
				 * @param href - href 对应的业务输入；具体类型和空值范围以方法签名为准。
				 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
				 */
				checkImage(src: string, alt: string, href: string): boolean | string | undefined {
					if (src.indexOf('http') !== 0) {
						return '图片网址必须以 http/https 开头';
					}
					return true;
				},
			},
		},
	},
	editorVal: props.getHtml,
});

// 编辑器回调函数
/**
 * 响应 index 的 handle Created 事件，并协调相关状态和后续操作。
 * @param editor - editor 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleCreated = (editor: IDomEditor) => {
	editorRef.value = editor;
};
// 编辑器内容改变时
/**
 * 响应 index 的 handle Change 事件，并协调相关状态和后续操作。
 * @param editor - editor 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleChange = (editor: IDomEditor) => {
	emit('update:getHtml', editor.getHtml());
	emit('update:getText', editor.getText());
};
// 页面销毁时
onBeforeUnmount(() => {
	const editor = editorRef.value;
	if (editor == null) return;
	editor.destroy();
});
// 监听是否禁用改变
// https://gitee.com/lyt-top/vue-next-admin/issues/I4LM7I
watch(
	() => props.disable,
	(bool) => {
		const editor = editorRef.value;
		if (editor == null) return;
		bool ? editor.disable() : editor.enable();
	},
	{
		deep: true,
	}
);
// 监听双向绑定值改变，用于回显
watch(
	() => props.getHtml,
	(val) => {
		state.editorVal = val;
	},
	{
		deep: true,
	}
);

// 暴露 editorRef
defineExpose({
	ref: editorRef,
});
</script>
<style lang="less">
.editor-container {
	overflow-y: hidden;
	.w-e-bar-item {
		.w-e-select-list {
			height: 150px;
			z-index: 10 !important;
		}
	}
	.w-e-text-container {
		// 文本框里面的层级调低
		//z-index: 3 !important;
	}
	.w-e-toolbar {
		// 给工具栏换行
		flex-wrap: wrap;
		z-index: 4 !important;
	}
	.w-e-menu {
		// 最重要的一句代码
		z-index: auto !important;
		.w-e-droplist {
			// 触发工具栏后的显示框调高
			z-index: 2 !important;
		}
	}
}
</style>
