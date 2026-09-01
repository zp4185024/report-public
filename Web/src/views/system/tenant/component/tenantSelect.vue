<template v-if="userStore.userInfos.accountType == 999">
    <el-select v-bind="$attrs" v-model="tenantModelValue" placeholder="请选择租户">
        <el-option v-for="(item, index) in state.tenantList"
            :value="item.value" 
            :label="item.host ? `${item.label} (${item.host})` : item.label" 
            :key="index" 
        />
    </el-select>
</template>

<script lang="ts" setup name="TenantSelect">
import { PropType, computed, reactive, onMounted } from 'vue';

import { getAPI } from '/@/utils/axios-utils';
import { useUserInfo } from "/@/stores/userInfo";
import { SysTenantApi} from '/@/api-services/api';

const emit = defineEmits(['update:modelValue']);

const props = defineProps({
	/**
     * 绑定的值
     * @example
     * <tenant-select v-model="value" code="xxxx" />
   */
    modelValue: {
        type: [String, Number, null] as PropType<string | number | null>,
        default: null,
        required: true,
    },

});
const tenantModelValue = computed({
    /**
     * 查询或读取 tenantSelect 所需的 get 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    get: () => props.modelValue,
    /**
     * 更新 tenantSelect 的 set 状态，并同步相关业务数据。
     * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    set: (val) => emit('update:modelValue', val),
});

const userStore = useUserInfo();
const state = reactive({
    tenantList: [] as Array<any>,
});


onMounted( async () => {
    if (userStore.userInfos.accountType == 999) {
        state.tenantList = await getAPI(SysTenantApi).apiSysTenantListGet().then(res => res.data.result ?? []);
    }
});
</script>

<style lang="scss" scoped></style>
