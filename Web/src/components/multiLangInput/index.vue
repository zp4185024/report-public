<template>
    <div class="multi-lang-input">
        <el-input v-model="inputModelValue" :placeholder="`请输入 ${currentLangLabel}`" clearable
            @update:model-value="(val: string) => emit('update:modelValue', val)">
            <template #append>
                <el-button @click="openDialog" circle>
                    <template #icon>
                        <i class="iconfont icon-diqiu1"></i>
                    </template>
                </el-button>
            </template>
        </el-input>

        <el-dialog v-model="dialogVisible" title="多语言设置" draggable :close-on-click-modal="false" width="600px">
            <el-form ref="ruleFormRef" label-width="auto">
                <el-row :gutter="35">
                    <el-col v-for="lang in languages" :key="lang.code" :span="24" class="mb10">
                        <el-form-item :label="lang.label">
                            <el-input v-model="multiLangValue[lang.code]" :placeholder="`请输入: ${lang.label}`"
                                clearable />
                        </el-form-item>
                    </el-col>
                </el-row>
            </el-form>

            <template #footer>
                <el-button @click="aiTranslation">AI翻译</el-button>
                <el-button @click="closeDialog">关闭</el-button>
                <el-button type="primary" @click="confirmDialog">确认修改</el-button>
            </template>
        </el-dialog>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import { useLangStore } from "/@/stores/useLangStore";
import { Local } from "/@/utils/storage";
import { getAPI } from "/@/utils/axios-utils";
import { SysLangTextApi } from "/@/api-services/api";
import { ElMessage } from "element-plus";

const emit = defineEmits<{
    (e: "update:modelValue", value?: string | null): void;
}>();

const ruleFormRef = ref();

const props = defineProps<{
    modelValue?: string | null;
    entityName: string;
    entityId: number;
    fieldName: string;
}>();

const inputModelValue = computed({
    /**
     * 查询或读取 index 所需的 get 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    get: () => props.modelValue,
    /**
     * 更新 index 的 set 状态，并同步相关业务数据。
     * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    set: (val) => emit("update:modelValue", val),
});
const langStore = useLangStore();
const languages = ref<any[]>([]);

const currentLang = ref("zh-CN");
const activeLang = ref("zh-CN");
const dialogVisible = ref(false);
const multiLangValue = ref<Record<string, string>>({});

const currentLangLabel = computed(() => {
    return (
        languages.value.find((l) => l.code === currentLang.value)?.label ||
        currentLang.value
    );
});

/**
 * 查询或读取 index 所需的 fetch Multi Lang 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const fetchMultiLang = async () => {
    const result = await getAPI(SysLangTextApi)
        .apiSysLangTextListPost({
            entityName: props.entityName,
            entityId: props.entityId,
            fieldName: props.fieldName,
            pageSize: 200,
        })
        .then((res) => res.data.result);
    return result ?? [];
};

onMounted(async () => {
    if (langStore.languages.length === 0) {
        await langStore.loadLanguages();
    }
    languages.value = langStore.languages;

    const themeConfig = Local.get("themeConfig");
    const globalI18n = themeConfig?.globalI18n;

    if (globalI18n) {
        const matched = langStore.languages.find(
            (l) => l.code === globalI18n || l.value === globalI18n
        );
        const langCode = matched?.code || "zh-CN";
        currentLang.value = langCode;
        activeLang.value = langCode;
    } else if (languages.value.length > 0) {
        currentLang.value = languages.value[0].code;
        activeLang.value = languages.value[0].code;
    }
});

/**
 * 负责 index 的 ai Translation 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const aiTranslation = async () => {
    for (const element of languages.value) {
        if (element.code === currentLang.value) continue;
        multiLangValue.value[element.code] = "正在翻译...";
        try {
            const text = await getAPI(SysLangTextApi)
                .apiSysLangTextAiTranslateTextPost({
                    originalText: props.modelValue,
                    targetLang: element.code,
                })
                .then((res) => res.data.result);

            multiLangValue.value[element.code] = text || "";
        } catch (e: any) {
            multiLangValue.value[element.code] = "";
            ElMessage.warning(e.message);
        }
    }
};

/**
 * 准备 index 的 open Dialog 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openDialog = async () => {
    if (!props.entityId) {
        ElMessage.warning("请先保存数据！");
        return;
    }
    const res = await fetchMultiLang();

    const newValues: Record<string, string> = {};
    res.forEach((element: { langCode?: string | null; content?: string | null }) => {
        if (element.langCode) {
            newValues[element.langCode] = element.content ?? "";
        }
    });

    newValues[currentLang.value] = props.modelValue ?? "";

    multiLangValue.value = newValues;
    dialogVisible.value = true;
    ruleFormRef.value?.resetFields();
};

/**
 * 结束 index 的 close Dialog 交互并清理临时状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const closeDialog = () => {
    dialogVisible.value = false;
    multiLangValue.value = {};
    ruleFormRef.value?.resetFields();
};

/**
 * 按 confirm Dialog 规则筛选 index 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const confirmDialog = async () => {
    const langItems = Object.entries(multiLangValue.value)
        .filter(([_, content]) => content && content.trim() !== "")
        .map(([code, content]) => ({
            entityName: props.entityName,
            entityId: props.entityId,
            fieldName: props.fieldName,
            langCode: code,
            content: content,
        }));

    if (langItems.length === 0) {
        ElMessage.warning("请输入至少一条多语言内容！");
        return;
    }

    try {
        await getAPI(SysLangTextApi).apiSysLangTextBatchSavePost(langItems);
        ElMessage.success("保存成功！");
        emit(
            "update:modelValue",
            multiLangValue.value[currentLang.value] ?? props.modelValue
        );
        dialogVisible.value = false;
    } catch (err) {
        console.error(err);
        ElMessage.error("保存失败！");
    }
    dialogVisible.value = false;
    ruleFormRef.value?.resetFields();
};
</script>

<style lang="scss" scoped>
.multi-lang-input {
    width: 100%;
}

.mb10:last-child {
    margin-bottom: 0 !important;
}
</style>
