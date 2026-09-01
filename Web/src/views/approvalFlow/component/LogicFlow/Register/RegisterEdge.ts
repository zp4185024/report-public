import LogicFlow from "@logicflow/core";
// 引入自定义的边
import edgeSql from './Edges/EdgeSql';
// 注册边
/**
 * 校验 RegisterEdge 的 Register 输入后执行核心操作，并把成功或失败结果交给调用方。
 * @param lf - lf 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const Register = (lf: LogicFlow) => {
    lf.register(edgeSql);
};

export default { Register };