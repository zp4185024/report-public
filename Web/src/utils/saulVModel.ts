import { computed } from 'vue'
/**
 * 负责 saulVModel 的 saul VModel 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param props - props 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param propName - 需要读取、校验或转换的业务值。
 * @param emit - emit 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export const saulVModel = <T extends Readonly<{ [k: string]: any }>, K extends keyof T>(
  props: T,
  propName: K,
  emit: (...args: any[]) => void
) => {
  return computed({
    /**
     * 查询或读取 saulVModel 所需的 get 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    get() {
      if (typeof props[propName] === 'object') {
        return new Proxy(props[propName], {
          /**
           * 更新 saulVModel 的 set 状态，并同步相关业务数据。
           * @param obj - obj 对应的业务输入；具体类型和空值范围以方法签名为准。
           * @param name - 需要读取、校验或转换的业务值。
           * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
           * @returns 返回值与当前方法声明的类型及空值约束保持一致。
           */
          set(obj, name, val) {
            emit(`update:${String(propName)}`, { ...obj, [name]: val })
            return true
          }
        })
      }
      return props[propName]
    },
    /**
     * 更新 saulVModel 的 set 状态，并同步相关业务数据。
     * @param val - val 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    set(val) {
      emit(`update:${String(propName)}`, val)
    }
  })
}