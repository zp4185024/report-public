<template>
	<div>
		<el-dialog v-model="state.isShowDialog" width="769px" :before-close="onCancel">
			<template #header>
				<div style="color: #fff">
					<el-icon size="16" style="margin-right: 3px; display: inline; vertical-align: middle"> <ele-Edit /> </el-icon>
					<span>{{ props.title }}</span>
				</div>
			</template>
			<div class="cropper-warp">
				<div class="cropper-warp-left">
					<img :src="state.cropperImg" class="cropper-warp-left-img" />
				</div>
				<div class="cropper-warp-right">
					<div class="cropper-warp-right-title">预览</div>
					<div class="cropper-warp-right-item">
						<div class="cropper-warp-right-value">
							<img :src="state.cropperImgBase64" class="cropper-warp-right-value-img" />
						</div>
						<div class="cropper-warp-right-label">100 x 100</div>
					</div>
					<div class="cropper-warp-right-item">
						<div class="cropper-warp-right-value">
							<img :src="state.cropperImgBase64" class="cropper-warp-right-value-img cropper-size" />
						</div>
						<div class="cropper-warp-right-label">50 x 50</div>
					</div>
				</div>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-upload
						ref="uploadSignRef"
						accept=".jpg,.png"
						:limit="1"
						:show-file-list="false"
						:auto-upload="false"
						:on-change="selectPicture"
						:on-exceed="selectPictureExceed"
					>
						<el-button icon="ele-Picture">选择图片</el-button>
					</el-upload>
					<el-button @click="onCancel">取 消</el-button>
					<el-button type="primary" @click="onSubmit">确 定</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts" name="cropper">
import { reactive, nextTick, ref } from 'vue';
import Cropper from 'cropperjs';
import 'cropperjs/dist/cropper.css';
import { genFileId } from 'element-plus';
import type { UploadInstance, UploadProps, UploadRawFile } from 'element-plus';

const props = defineProps({
	title: {
		type: String,
		/**
		 * 为 title 属性创建独立默认值，避免组件实例之间共享可变数据。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		default: () => '',
	},
});
const emits = defineEmits(['uploadCropperImg']);
const uploadSignRef = ref<UploadInstance>();
// 定义变量内容
const state = reactive({
	isShowDialog: false,
	cropperImg: '',
	cropperImgBase64: '',
	cropper: '' as RefType,
});

// 打开弹窗
/**
 * 准备 index 的 open Dialog 交互状态并打开对应界面。
 * @param imgs - imgs 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = (imgs: string) => {
	state.cropperImg = imgs;
	state.isShowDialog = true;
	nextTick(() => {
		initCropper();
	});
};
// 关闭弹窗
/**
 * 结束 index 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
	state.cropper.destroy();
	state.isShowDialog = false;
};
// 取消
/**
 * 响应 index 的 on Cancel 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onCancel = () => {
	closeDialog();
};
// 更换/上传
/**
 * 响应 index 的 on Submit 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const onSubmit = async () => {
	const img = await getCroppedCanvas();
	emits('uploadCropperImg', { img: img });
	closeDialog();
};
// 初始化cropperjs图片裁剪
/**
 * 初始化 index 的 init Cropper 状态，创建依赖并注册运行期间需要的资源。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const initCropper = () => {
	const letImg = <HTMLImageElement>document.querySelector('.cropper-warp-left-img');
	if (letImg) {
		letImg.setAttribute('crossOrigin', 'anonymous');
	}
	state.cropper = new Cropper(letImg, {
		viewMode: 1,
		dragMode: 'none',
		initialAspectRatio: 1,
		aspectRatio: 1,
		preview: '.before',
		background: true,
		autoCropArea: 1,
		// zoomOnWheel: false,
		checkCrossOrigin: false,
		/**
		 * 负责 index 的 crop 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
		 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
		 */
		crop: () => {
			state.cropperImgBase64 = state.cropper.getCroppedCanvas()!.toDataURL('image/jpeg');
		},
	});
};

// 获取裁切后的图片 (包装为Promise)
/**
 * 查询或读取 index 所需的 get Cropped Canvas 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const getCroppedCanvas = () => {
	return new Promise((resolve) => {
		state.cropper.getCroppedCanvas().toBlob((blob: any) => {
			resolve(blob);
		});
	});
};

// 选择图片
/**
 * 校验 index 的 select Picture 文件输入，读取内容并更新目标数据或预览状态。
 * @param file - file 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const selectPicture = async (file: any) => {
	let URL = window.URL || window.webkitURL;
	state.cropperImg = URL.createObjectURL(file.raw);
	state.cropper.replace(state.cropperImg);
};

// 选择图片超出数量限制时执行
/**
 * 校验 index 的 select Picture Exceed 文件输入，读取内容并更新目标数据或预览状态。
 * @param files - files 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const selectPictureExceed: UploadProps['onExceed'] = (files) => {
	uploadSignRef.value!.clearFiles();
	const file = files[0] as UploadRawFile;
	file.uid = genFileId();
	uploadSignRef.value!.handleStart(file);
};

// 暴露变量
defineExpose({
	openDialog,
});
</script>

<style scoped lang="scss">
.cropper-warp {
	display: flex;
	.cropper-warp-left {
		position: relative;
		display: inline-block;
		height: 350px;
		flex: 1;
		border: 1px solid var(--el-border-color);
		background: var(--el-color-white);
		overflow: hidden;
		background-repeat: no-repeat;
		cursor: move;
		border-radius: var(--el-border-radius-base);
		.cropper-warp-left-img {
			width: 100%;
			height: 100%;
		}
	}
	.cropper-warp-right {
		width: 150px;
		height: 350px;
		.cropper-warp-right-title {
			text-align: center;
			height: 20px;
			line-height: 20px;
		}
		.cropper-warp-right-item {
			margin: 15px 0;
			.cropper-warp-right-value {
				display: flex;
				.cropper-warp-right-value-img {
					width: 100px;
					height: 100px;
					border-radius: var(--el-border-radius-circle);
					margin: auto;
				}
				.cropper-size {
					width: 50px;
					height: 50px;
				}
			}
			.cropper-warp-right-label {
				text-align: center;
				font-size: 12px;
				color: var(--el-text-color-primary);
				height: 30px;
				line-height: 30px;
			}
		}
	}
}
.dialog-footer {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 12px;

	.el-button+.el-button {
		margin-left: 0 !important;
	}
}
</style>
