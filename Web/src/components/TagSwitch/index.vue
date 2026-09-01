<template>
    <div class="tagsw">
        <el-switch size="small" class="tagsw-switch" 
            v-model="tgswModelValue"
            :active-value="activeValue"
            :inactive-value="inactiveValue" 
            @change="handleChange"
        />
        <span class="tagsw-tage">
            <GSysDict v-model="tgswModelValue" :code="code" />
        </span>
    </div>
</template>

<script lang="ts" setup name="TagSwitch">
import { computed, PropType } from 'vue';
import GSysDict from "/@/components/sysDict/sysDict.vue";

const emit = defineEmits(['change', 'update:modelValue']);

const props = defineProps({
    /**
     * 绑定的值，支持多种类型
     * @example
     * <tag-switch v-model="value" code="xxxx" />
   */
    modelValue: {
        type: [String, Number, Boolean, Array, null] as PropType<string | number | boolean | any[] | null>,
        default: null,
        required: true,
    },

    /**
     * 字典编码，用于获取字典项 (同sys-dict)
     * @example 'gender'
   */
    code: {
        type: String,
        required: true,
    },
    
    /**
     * switch 状态为 on 时的值，默认true (同el-switch)
     * @example true
     */
    activeValue: {
        type: [String, Number, Boolean] as PropType<string | number | boolean>,
        default: true,
    },
    /**
     * switch的状态为 off 时的值，默false (同el-switch)
     * @example false
     */
    inactiveValue: {
        type: [String, Number, Boolean] as PropType<string | number | boolean>,
        default: false,
    },
});
const tgswModelValue = computed({
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
    set: (val) => emit('update:modelValue', val),
});


/**
 * 响应 index 的 handle Change 事件，并协调相关状态和后续操作。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleChange = (val: any) => {
    emit('change', val);
};
</script>

<style lang="scss" scoped>
.tagsw {
    //width: 100%;

    .tagsw-switch {
        display: none;
        height: 100%;
        line-height: 100%;
    }

    .tagsw-tage {
        display: block;
    }
}
.tagsw:hover {
    .tagsw-switch {
        display: inline-flex;
    }

    .tagsw-tage {
        display: none;
    }
}
</style>
