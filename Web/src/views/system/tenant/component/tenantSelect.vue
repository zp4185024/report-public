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
    get: () => props.modelValue,
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
