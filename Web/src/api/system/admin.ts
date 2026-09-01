import request from '/@/utils/request';
enum Api {
	DictTypeDataList = '/api/sysDictData/DataList',
	AllDictList = '/api/sysDictType/AllDictList',
	HardwareInfo = '/api/sysServer/hardwareInfo',
	RuntimeInfo = '/api/sysServer/runtimeInfo',
	NuGetPackagesInfo = '/api/sysServer/nuGetPackagesInfo',
}

// 根据字典类型编码获取字典值集合
/**
 * 查询或读取 admin 所需的 get Dict Data List 数据，并按调用约定返回结果。
 * @param params - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getDictDataList = (params?: any) =>
	request({
		url: `${Api.DictTypeDataList}/${params}`,
		method: 'get',
	});

// 获取所有字典
/**
 * 查询或读取 admin 所需的 get All Dict List 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getAllDictList = () =>
	request({
		url: `${Api.AllDictList}`,
		method: 'get',
	});

	// 获取硬件信息
/**
 * 查询或读取 admin 所需的 get Hardware Info 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getHardwareInfo = () =>
	request({
		url: `${Api.HardwareInfo}`,
		method: 'post',
	});

// 获取运行时信息
/**
 * 查询或读取 admin 所需的 get Runtime Info 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getRuntimeInfo = () =>
	request({
		url: `${Api.RuntimeInfo}`,
		method: 'post',
	});

// 获取NuGet包信息
/**
 * 查询或读取 admin 所需的 get Nu Get Packages Info 数据，并按调用约定返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const getNuGetPackagesInfo = () =>
	request({
		url: `${Api.NuGetPackagesInfo}`,
		method: 'post',
	});

