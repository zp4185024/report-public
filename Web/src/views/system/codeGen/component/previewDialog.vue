<template>
	<div class="sys-codeGenPreview-container">
		<el-dialog v-model="state.isShowDialog" draggable :close-on-click-modal="false" width="70%">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span> {{ props.title }} </span>
				</div>
			</template>
			<div :class="[state.current?.endsWith('.cs') ? 'cs-style' : state.current?.endsWith('.vue') ? 'vue-style' : 'js-style']">
				<el-segmented v-model="state.current" :options="state.options" block @change="handleChange">
					<template #default="{ item }">
						<div class="pd4">
							<SvgIcon :name="item.icon" class="mb4" />
							<div>{{ item.value }}</div>
						</div>
					</template>
				</el-segmented>
			</div>
			<div ref="monacoEditorRef" v-loading="state.loading" class="code-container"></div>
			<template #footer>
				<span class="dialog-footer">
					<el-button icon="ele-Close" @click="cancel">关 闭</el-button>
					<el-button icon="ele-CopyDocument" type="primary" @click="handleCopy">复 制</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script lang="ts" setup name="sysPreviewCode">
import { reactive, ref, nextTick, toRaw } from 'vue';
import * as monaco from 'monaco-editor';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import commonFunction from '/@/utils/commonFunction';

import { getAPI } from '/@/utils/axios-utils';
import { SysCodeGenApi } from '/@/api-services/api';

const { copyText } = commonFunction();

const props = defineProps({
	title: String,
});
const monacoEditorRef = ref();
const state = reactive({
	isShowDialog: false,
	options: [] as any, // 分段器的选项
	current: '', // 选中的分段
	codes: [] as any, // 预览的代码
  loading: true
});

// 防止 monaco 报黄
self.MonacoEnvironment = {
	/**
	 * 查询或读取 previewDialog 所需的 get Worker 数据，并按调用约定返回结果。
	 * @param _ - _ 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @param label - label 对应的业务输入；具体类型和空值范围以方法签名为准。
	 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
	 */
	getWorker: (_: string, label: string) => new EditorWorker(),
};

// 初始化monacoEditor对象
var monacoEditor: any = null;
/**
 * 初始化 previewDialog 的 init Monaco Editor 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initMonacoEditor = () => {
	monacoEditor = monaco.editor.create(monacoEditorRef.value, {
		theme: 'vs-dark', // 主题 vs vs-dark hc-black
		value: '', // 默认显示的值
		language: 'csharp',
		formatOnPaste: true,
		wordWrap: 'on', // 自动换行，注意大小写
		wrappingIndent: 'indent',
		folding: true, // 是否折叠
		foldingHighlight: true, // 折叠等高线
		foldingStrategy: 'indentation', // 折叠方式  auto | indentation
		showFoldingControls: 'always', // 是否一直显示折叠 always | mouSEOver
		disableLayerHinting: true, // 等宽优化
		emptySelectionClipboard: false, // 空选择剪切板
		selectionClipboard: false, // 选择剪切板
		automaticLayout: true, // 自动布局
		codeLens: false, // 代码镜头
		scrollBeyondLastLine: false, // 滚动完最后一行后再滚动一屏幕
		colorDecorators: true, // 颜色装饰器
		accessibilitySupport: 'auto', // 辅助功能支持  "auto" | "off" | "on"
		lineNumbers: 'on', // 行号 取值： "on" | "off" | "relative" | "interval" | function
		lineNumbersMinChars: 5, // 行号最小字符   number
		//enableSplitViewResizing: false,
		readOnly: false, // 是否只读  取值 true | false
	});
};

// 打开弹窗
/**
 * 准备 previewDialog 的 open Dialog 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = async (row: any) => {
  state.loading = true;
  try {
    state.isShowDialog = true;
    const { data } = await getAPI(SysCodeGenApi).apiSysCodeGenPreviewPost(row);
    state.codes = data.result ?? [];
    state.options = Object.keys(data.result ?? []).map((fileName: string) => ({
      value: fileName,
      icon: fileName?.endsWith('.cs') ? 'fa fa-hashtag' : fileName?.endsWith('.vue') ? 'fa fa-vimeo' : 'fa fa-file-code-o',
    }));
    state.current = state.options?.[0]?.value ?? '';
  }  catch (e) { /* empty */ }
  state.loading = false;
	if (monacoEditor == null) initMonacoEditor();
	// 防止取不到
	nextTick(() => {
		monacoEditor.setValue(state.codes[state.current]);
	});
};

// 分段器改变时切换代码
/**
 * 响应 previewDialog 的 handle Change 事件，并协调相关状态和后续操作。
 * @param current - current 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleChange = (current: any) => {
	monacoEditor.setValue(state.codes[current]);
};

// 取消
/**
 * 校验 previewDialog 的 cancel 条件，并返回可供后续流程判断的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const cancel = () => {
	state.isShowDialog = false;
};

//复制代码
/**
 * 响应 previewDialog 的 handle Copy 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleCopy = () => {
	copyText(state.codes[state.current]);
};

// 导出对象
defineExpose({ openDialog });
</script>

<style lang="scss" scoped>
.cs-style .el-segmented {
	--el-segmented-item-selected-bg-color: #5c2d91;
}
.vue-style .el-segmented {
	--el-segmented-item-selected-bg-color: #42b883;
}
.js-style .el-segmented {
	--el-segmented-item-selected-bg-color: #e44d26;
}
:deep(.el-dialog__body) {
	height: calc(100vh - 160px) !important;

    .code-container {
        width: 100%;
        height: calc(100% - 48px);
    }
}
</style>
