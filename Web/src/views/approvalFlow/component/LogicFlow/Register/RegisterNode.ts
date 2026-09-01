import LogicFlow from "@logicflow/core";
// 引入自定义节点
import nodeStart from './Nodes/NodeStart';
import nodeEnd from './Nodes/NodeEnd';
import nodeTask from './Nodes/NodeTask';
import nodeUser from './Nodes/NodeUser';
import nodeSql from './Nodes/NodeSql';
// 注册节点
/**
 * 校验 RegisterNode 的 Register 输入后执行核心操作，并把成功或失败结果交给调用方。
 * @param lf - lf 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
const Register = (lf: LogicFlow) => {
    lf.register(nodeStart);
    lf.register(nodeEnd);
    lf.register(nodeTask);
    lf.register(nodeUser);
    lf.register(nodeSql);
};

export default { Register };