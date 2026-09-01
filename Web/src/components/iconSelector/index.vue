<template>
	<div class="icon-selector w100 h100">
		<el-input v-model="state.fontIconSearch" :placeholder="state.fontIconPlaceholder" :clearable="clearable" :disabled="disabled" :size="size" ref="inputWidthRef" @clear="onClearFontIcon">
			<template #prepend>
				<SvgIcon
					:name="state.fontIconPrefix === '' ? prepend : state.fontIconPrefix"
					class="font14"
					v-if="state.fontIconPrefix === '' ? prepend?.indexOf('ele-') > -1 : state.fontIconPrefix?.indexOf('ele-') > -1"
				/>
				<i v-else :class="state.fontIconPrefix === '' ? prepend : state.fontIconPrefix" class="font14"></i>
			</template>
		</el-input>
		<el-popover placement="bottom" :width="state.fontIconWidth" transition="el-zoom-in-top" popper-class="icon-selector-popper" trigger="click" :virtual-ref="inputWidthRef" virtual-triggering>
			<template #default>
				<div class="icon-selector-warp">
					<div class="icon-selector-warp-title">{{ title }}</div>
					<el-tabs v-model="state.fontIconTabActive" @tab-click="onIconClick">
						<el-tab-pane lazy label="ali" name="ali">
							<IconList :list="fontIconSheetsFilterList" :empty="emptyDescription" :prefix="state.fontIconPrefix" @get-icon="onColClick" />
						</el-tab-pane>
						<el-tab-pane lazy label="ele" name="ele">
							<IconList :list="fontIconSheetsFilterList" :empty="emptyDescription" :prefix="state.fontIconPrefix" @get-icon="onColClick" />
						</el-tab-pane>
						<el-tab-pane lazy label="awe" name="awe">
							<IconList :list="fontIconSheetsFilterList" :empty="emptyDescription" :prefix="state.fontIconPrefix" @get-icon="onColClick" />
						</el-tab-pane>
					</el-tabs>
				</div>
			</template>
		</el-popover>
	</div>
</template>

<script setup lang="ts" name="iconSelector">
import { defineAsyncComponent, ref, reactive, onMounted, nextTick, computed, watch } from 'vue';
import type { TabsPaneContext } from 'element-plus';
import initIconfont from '/@/utils/getStyleSheets';
import '/@/theme/iconSelector.scss';

// 定义父组件传过来的值
const props = defineProps({
	// 输入框前置内容
	prepend: {
		type: String,
		/**
		 * 为 prepend 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => 'ele-Pointer',
	},
	// 输入框占位文本
	placeholder: {
		type: String,
		/**
		 * 为 placeholder 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '请输入内容搜索图标或者选择图标',
	},
	// 输入框占位文本
	size: {
		type: String,
		/**
		 * 为 size 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => 'default',
	},
	// 弹窗标题
	title: {
		type: String,
		/**
		 * 为 title 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '请选择图标',
	},
	// 禁用
	disabled: {
		type: Boolean,
		/**
		 * 为 disabled 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => false,
	},
	// 是否可清空
	clearable: {
		type: Boolean,
		/**
		 * 为 clearable 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => true,
	},
	// 自定义空状态描述文字
	emptyDescription: {
		type: String,
		/**
		 * 为 emptyDescription 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '无相关图标',
	},
	// 双向绑定值，默认为 modelValue，
	// 参考：https://v3.cn.vuejs.org/guide/migration/v-model.html#%E8%BF%81%E7%A7%BB%E7%AD%96%E7%95%A5
	// 参考：https://v3.cn.vuejs.org/guide/component-custom-events.html#%E5%A4%9A%E4%B8%AA-v-model-%E7%BB%91%E5%AE%9A
	modelValue: String,
});

// 定义子组件向父组件传值/事件
const emit = defineEmits(['update:modelValue', 'get', 'clear']);

// 引入组件
const IconList = defineAsyncComponent(() => import('/@/components/iconSelector/list.vue'));

// 定义变量内容
const inputWidthRef = ref();
const state = reactive({
	fontIconPrefix: '',
	fontIconWidth: 0,
	fontIconSearch: '',
	fontIconPlaceholder: '',
	fontIconTabActive: 'ali',
	fontIconList: {
		ali: [],
		ele: [],
		awe: [],
	},
});

// 图标搜索及图标数据显示
const fontIconSheetsFilterList = computed(() => {
	const list = fontIconTabNameList();
	if (!state.fontIconSearch) return list;
	let search = state.fontIconSearch.trim().toLowerCase();
	return list.filter((item: string) => {
		if (item.toLowerCase().indexOf(search) !== -1) return item;
	});
});
// 根据 tab name 类型设置图标
/**
 * 负责 index 的 font Icon Tab Name List 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const fontIconTabNameList = () => {
	let iconList: any = [];
	if (state.fontIconTabActive === 'ali') iconList = state.fontIconList.ali;
	else if (state.fontIconTabActive === 'ele') iconList = state.fontIconList.ele;
	else if (state.fontIconTabActive === 'awe') iconList = state.fontIconList.awe;
	return iconList;
};
// 处理 icon 双向绑定数值回显
/**
 * 初始化 index 的 init Mode Value Echo 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initModeValueEcho = () => {
	if (props.modelValue === '') return ((<string | undefined>state.fontIconPlaceholder) = props.placeholder);
	(<string | undefined>state.fontIconPlaceholder) = props.modelValue;
	(<string | undefined>state.fontIconPrefix) = props.modelValue;
};
// 处理 icon 类型，用于回显时，tab 高亮与初始化数据
/**
 * 初始化 index 的 init Font Icon Name 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initFontIconName = () => {
	let name = 'ali';
	if (props.modelValue == undefined) name = 'ele';
	else if (props.modelValue!.indexOf('iconfont') > -1) name = 'ali';
	else if (props.modelValue!.indexOf('ele-') > -1) name = 'ele';
	else if (props.modelValue!.indexOf('fa') > -1) name = 'awe';
	// 初始化 tab 高亮回显
	state.fontIconTabActive = name;
	return name;
};
// 初始化数据
/**
 * 遍历 index 数据并执行 init Font Icon Data 转换，生成后续流程需要的结果。
 * @param name - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initFontIconData = async (name: string) => {
	if (name === 'ali') {
		// 阿里字体图标使用 `iconfont xxx`
		if (state.fontIconList.ali.length > 0) return;
		await initIconfont.ali().then((res: any) => {
			state.fontIconList.ali = res.map((i: string) => `iconfont ${i}`);
		});
	} else if (name === 'ele') {
		// element plus 图标
		if (state.fontIconList.ele.length > 0) return;
		await initIconfont.ele().then((res: any) => {
			state.fontIconList.ele = res;
		});
	} else if (name === 'awe') {
		// fontawesome字体图标使用 `fa xxx`
		if (state.fontIconList.awe.length > 0) return;
		await initIconfont.awe().then((res: any) => {
			state.fontIconList.awe = res.map((i: string) => `fa ${i}`);
		});
	}
	// 初始化 input 的 placeholder
	// 参考（单项数据流）：https://cn.vuejs.org/v2/guide/components-props.html?#%E5%8D%95%E5%90%91%E6%95%B0%E6%8D%AE%E6%B5%81
	state.fontIconPlaceholder = props.placeholder;
	// 初始化双向绑定回显
	initModeValueEcho();
};
// 图标点击切换
/**
 * 响应 index 的 on Icon Click 事件，并协调相关状态和后续操作。
 * @param pane - pane 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onIconClick = (pane: TabsPaneContext) => {
	initFontIconData(pane.paneName as string);
	inputWidthRef.value.focus();
};
// 获取当前点击的 icon 图标
/**
 * 响应 index 的 on Col Click 事件，并协调相关状态和后续操作。
 * @param v - v 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onColClick = (v: string) => {
	state.fontIconPlaceholder = v;
	state.fontIconPrefix = v;
	emit('get', state.fontIconPrefix);
	emit('update:modelValue', state.fontIconPrefix);
	inputWidthRef.value.focus();
};
// 清空当前点击的 icon 图标
/**
 * 响应 index 的 on Clear Font Icon 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onClearFontIcon = () => {
	state.fontIconPrefix = '';
	emit('clear', state.fontIconPrefix);
	emit('update:modelValue', state.fontIconPrefix);
};
// 获取 input 的宽度
/**
 * 查询或读取 index 所需的 get Input Width 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const getInputWidth = () => {
	nextTick(() => {
		state.fontIconWidth = inputWidthRef.value.$el.offsetWidth;
	});
};
// 监听页面宽度改变
/**
 * 初始化 index 的 init Resize 监听流程，并在状态变化时同步执行相关操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initResize = () => {
	window.addEventListener('resize', () => {
		getInputWidth();
	});
};
// 页面加载时
onMounted(() => {
	initFontIconData(initFontIconName());
	initResize();
	getInputWidth();
});
// 监听双向绑定 modelValue 的变化
watch(
	() => props.modelValue,
	() => {
		state.fontIconSearch = '';
		initModeValueEcho();
		initFontIconName();
		getInputWidth();
	}
);
</script>

<style scoped>
:deep(.el-tabs__nav) {
	float: right !important;
}
</style>
