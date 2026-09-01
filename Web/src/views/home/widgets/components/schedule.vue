<template>
	<card-pro shadow="hover" title="我的日程" prefix-icon="ele-Calendar" class="item-background">
		<template #suffix>
			<el-button type="primary" icon="ele-CirclePlus" round plain @click="openAddSchedule" style="float: right">添加日程</el-button>
		</template>

		<div class="custome-canlendar">
			<el-calendar v-model="state.calendarValue" ref="calendar">
				<template #header="{ date }">
					<span>{{ date }}</span>
					<el-button-group>
						<el-button size="small" @click="selectDate('prev-month')"> 上个月 </el-button>
						<el-button size="small" @click="selectDate('today')">今天</el-button>
						<el-button size="small" @click="selectDate('next-month')"> 下个月 </el-button>
					</el-button-group>
				</template>
				<template #date-cell="{ data }">
					<div @click="handleClickDate(data)">
						<div class="spandate">{{ data.day.split('-').slice(2).join('-') }}</div>
						<div v-for="(item, key) in state.ScheduleData" :key="key">
							<el-badge v-if="FormatDate(data.day) == FormatDate(item.scheduleTime)" is-dot class="item"></el-badge>
						</div>
						<div style="font-size: 10px">
							{{ solarDate2lunar(data.day) }}
						</div>
					</div>
				</template>
			</el-calendar>
		</div>
		<div class="schedule-list">
			<div class="item" v-for="(item, index) in state.TodayScheduleData" :key="index">
				<el-icon v-if="item.status == 1" class="icon" @click="changeStatus(item)"> <ele-CircleCheck /> </el-icon>
				<el-icon v-else class="icon" @click="changeStatus(item)"> <ele-Edit /> </el-icon>

				<span class="content" style="padding-left: 10px" @click="openEditSchedule(item)">
					<span> {{ item.startTime }} - {{ item.endTime }} </span>
					<span :class="item.status == 1 ? 'finish' : 'no'" style="padding-left: 15px; font-weight: 600; color: var(--el-color-primary)">
						{{ item.content }}
					</span>
				</span>
				<span style="float: right">
					<el-icon class="icon" @click="delItem(item)"> <ele-CircleClose /> </el-icon>
				</span>
			</div>
		</div>

		<EditSchedule ref="editScheduleRef" :title="state.editTitle" @handleQuery="handleQuery"> </EditSchedule>
	</card-pro>
</template>

<script lang="ts">
export default {
	title: '日程',
	icon: 'ele-Calendar',
	description: '日程演示',
};
</script>

<script setup lang="ts">
import { reactive, onMounted, ref } from 'vue';
import { dayjs, ElMessage, ElMessageBox } from 'element-plus';
import type { CalendarDateType, CalendarInstance } from 'element-plus';
import calendarCom from '/@/utils/calendar.js';

import CardPro from '/@/components/CardPro/index.vue';
import EditSchedule from '/@/views/home/widgets/components/scheduleEdit.vue';

import { getAPI } from '/@/utils/axios-utils';
import { SysScheduleApi } from '/@/api-services/api';
import { SysSchedule } from '/@/api-services/models';

const calendar = ref<CalendarInstance>();
const editScheduleRef = ref<InstanceType<typeof EditSchedule>>();
const state = reactive({
	ScheduleData: [] as Array<SysSchedule>, // 日程列表数据
	TodayScheduleData: [] as Array<SysSchedule>, // 当天列表数据
	calendarValue: new Date(),
	queryParams: {
		scheduleTime: new Date(),
		startTime: new Date(),
		endTime: new Date(),
	},
	editTitle: '',
	currentMonth: '',
});

// 页面初始化
onMounted(async () => {
	await handleQuery();
});

// 查询操作
/**
 * 响应 schedule 的 handle Query 事件，并协调相关状态和后续操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleQuery = async () => {
	state.queryParams.startTime = dayjs(GetMonthFirstDay(state.calendarValue)).add(-1, 'month').toDate();
	state.queryParams.endTime = dayjs(GetMonthLastDay(state.calendarValue)).add(1, 'month').toDate();

	let params = Object.assign(state.queryParams);
	var res = await getAPI(SysScheduleApi).apiSysSchedulePagePost(params);
	state.ScheduleData = res.data.result ?? [];
	// if (state.ScheduleData.length > 0) {
	state.TodayScheduleData = state.ScheduleData.filter((item) => {
		return FormatDate(item.scheduleTime) == FormatDate(state.calendarValue);
	});
	state.currentMonth = dayjs(state.calendarValue).format('YYYYMM');
};

/**
 * 负责 schedule 的 select Date 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const selectDate = async (val: CalendarDateType) => {
	if (!calendar.value) return;
	calendar.value.selectDate(val);
	await handleQuery();
};
// 删除
/**
 * 从 schedule 移除 del Item 对应数据，并清理相关缓存、选中项或临时状态。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const delItem = (row: any) => {
	ElMessageBox.confirm(`确定删日程：${row.startTime}-${row.endTime}【${row.content}】?`, '提示', {
		confirmButtonText: '确定',
		cancelButtonText: '取消',
		type: 'warning',
	})
		.then(async () => {
			await getAPI(SysScheduleApi).apiSysScheduleDeletePost(row);
			await handleQuery();
			ElMessage.success('删除成功');
		})
		.catch(() => {});
};

// 修改状态
/**
 * 更新 schedule 的 change Status 状态，并同步相关业务数据。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const changeStatus = async (row: any) => {
	await getAPI(SysScheduleApi)
		.apiSysScheduleSetStatusPost({ id: row.id, status: row.status == 1 ? 0 : 1 })
		.then(() => {
			row.status = row.status == 1 ? 0 : 1;
			ElMessage.success('日程状态设置成功');
		})
		.catch(() => {
			ElMessage.success('日程状态设置异常');
		});
};

// 农历转换
/**
 * 负责 schedule 的 solar Date2lunar 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param solarDate - solarDate 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const solarDate2lunar = (solarDate: any) => {
	var solar = solarDate.split('-');
	var lunar = calendarCom.solar2lunar(solar[0], solar[1], solar[2]);
	// return solar[1] + '-' + solar[2] + '\n' + lunar.IMonthCn + lunar.IDayCn;
	return lunar.IMonthCn + lunar.IDayCn;
};

// 按天查询
/**
 * 响应 schedule 的 handle Query By Date 事件，并协调相关状态和后续操作。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleQueryByDate = async (date: any) => {
	state.queryParams.startTime = FormatDateDelHMS(date);
	state.queryParams.endTime = FormatDateEndHMS(date);
	let params = Object.assign(state.queryParams);
	var res = await getAPI(SysScheduleApi).apiSysSchedulePagePost(params);
	state.TodayScheduleData = res.data.result ?? [];
};

// 打开新增页面
/**
 * 准备 schedule 的 open Add Schedule 交互状态并打开对应界面。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openAddSchedule = () => {
	var timerange = GetRecentTime();

	state.editTitle = '添加日程';
	editScheduleRef.value?.openDialog({ id: undefined, status: 0, orderNo: 100, scheduleTime: state.queryParams.scheduleTime, startTime: timerange.startTime, endTime: timerange.endTime });
};

// 打开编辑页面
/**
 * 准备 schedule 的 open Edit Schedule 交互状态并打开对应界面。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const openEditSchedule = async (row: any) => {
	if (row.status == 1) return;
	state.editTitle = '编辑日程';
	editScheduleRef.value?.openDialog(row, true);
};

// 点击日历中的日期
/**
 * 响应 schedule 的 handle Click Date 事件，并协调相关状态和后续操作。
 * @param data - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const handleClickDate = async (data: any) => {
	if (state.currentMonth != dayjs(data.day).format('YYYYMM')) {
		await handleQuery();
	}
	await handleQueryByDate(data.day);
	state.queryParams.scheduleTime = data.day;
};

// 获取当月第一天
/**
 * 查询或读取 schedule 所需的 Get Month First Day 数据，并按调用约定返回结果。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const GetMonthFirstDay = (date: any) => {
	var newDate = new Date(date);
	newDate.setDate(1);
	newDate.setHours(0);
	newDate.setMinutes(0);
	newDate.setSeconds(0);
	return newDate;
};

// 获取当月最后一天
/**
 * 查询或读取 schedule 所需的 Get Month Last Day 数据，并按调用约定返回结果。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const GetMonthLastDay = (date: any) => {
	var newDate = new Date(date);
	newDate.setMonth(newDate.getMonth() + 1);
	newDate.setDate(0);
	newDate.setHours(0);
	newDate.setMinutes(0);
	newDate.setSeconds(0);
	return newDate;
};

// 去掉时分秒的日期
/**
 * 将 schedule 中的 Format Date Del HMS 输入转换为调用方需要的稳定格式。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const FormatDateDelHMS = (date: any) => {
	var newDate = new Date(date);
	newDate.setHours(0);
	newDate.setMinutes(0);
	newDate.setSeconds(0);
	return newDate;
};

/**
 * 将 schedule 中的 Format Date End HMS 输入转换为调用方需要的稳定格式。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const FormatDateEndHMS = (date: any) => {
	var newDate = new Date(date);
	newDate.setHours(23);
	newDate.setMinutes(59);
	newDate.setSeconds(59);
	return newDate;
};

// 格式化日期
/**
 * 将 schedule 中的 Format Date 输入转换为调用方需要的稳定格式。
 * @param date - date 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const FormatDate = (date: any) => {
	return dayjs(date).format('YYYY-MM-DD');
};

// 获取最近的初始时间  EndTime默认为StartTime + 1(hour)
/**
 * 查询或读取 schedule 所需的 Get Recent Time 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const GetRecentTime = () => {
	var date = new Date();
	// 计算最近的开始时间
	var currentHour = date.getHours();
	var currentMin = date.getMinutes();

	var starHour = dayjs(date).format('HH');
	var endHour = dayjs(date).format('HH');
	var starMin = '00';
	var endMin = '00';
	// 如果当前时间已经23 那么starHour和endHour都是23
	if (currentHour == 23) {
		starHour = '23';
		endHour = '23';
		starMin = '00';
		endMin = '45';
	} else {
		// 判断分钟数属于那个层级
		if (currentMin < 15) {
			starMin = '15';
			endMin = '15';
			// 计算结束时间
			date.setHours(date.getHours() + 1);
			endHour = dayjs(date).format('HH');
		} else if (currentMin >= 15 && currentMin < 30) {
			starMin = '30';
			endMin = '30';

			// 计算结束时间
			date.setHours(date.getHours() + 1);
			endHour = dayjs(date).format('HH');
		} else if (currentMin >= 30 && currentMin < 45) {
			starMin = '45';
			endMin = '45';
			// 计算结束时间
			date.setHours(date.getHours() + 1);
			endHour = dayjs(date).format('HH');
		} else if (currentMin >= 45) {
			// 分钟 : 00
			starMin = '00';
			endMin = '00';

			// 开始时间+1
			date.setHours(date.getHours() + 1);
			starHour = dayjs(date).format('HH');
			// 计算结束时间
			date.setHours(date.getHours() + 1);
			endHour = dayjs(date).format('HH');
		}
	}
	return { startTime: starHour + ':' + starMin, endTime: endHour + ':' + endMin };
};
</script>

<style lang="scss" scoped>
.custome-canlendar {
	position: relative;
	text-align: center;

	:deep(.el-calendar) {
		.el-calendar-table .el-calendar-day {
			width: 100%;
			height: 100%;
		}

		.el-calendar__body {
			padding: 5px 0;
		}

		.el-calendar-table .el-calendar-day {
			position: relative;
		}

		td .spandate {
			margin: auto;
			width: 26px;
			height: 26px;
			line-height: 26px;
			border-radius: 50%;
		}
		td.is-selected .spandate {
			width: 26px;
			height: 26px;
			line-height: 26px;
			border-radius: 50%;
			color: #fff;
			background-color: var(--el-color-primary);
		}
		/*小红点样式*/
		.el-badge {
			position: absolute;
			left: 0;
			bottom: -8px;
			width: 100%;
		}
	}
}

// 日程列表
.schedule-list {
	overflow-y: auto;
	height: 150px;
	.item {
		position: relative;
		margin-bottom: 5px;
		padding: 0 11px;
		line-height: 24px;
		background-color: #f1f1f1;
		cursor: pointer;

		&::before {
			position: absolute;
			left: 0;
			top: 0;
			height: 100%;
			content: '';
			width: 3px;
			background: var(--el-color-primary);
		}

		.date {
			margin-right: 5px;
			font-size: 14px;
		}
		.content {
			color: #666;
			font-size: 14px;
		}
		.icon {
			display: inline;
			vertical-align: middle;
			color: var(--el-color-primary);
		}
		.finish {
			text-decoration: line-through 2px var(--el-color-danger) !important;
		}
	}
}
</style>
