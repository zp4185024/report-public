import { RectNode, RectNodeModel, h } from "@logicflow/core";

class TaskNode extends RectNode {
    /**
     * 查询或读取 NodeTask 所需的 get Share 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getShare() {
        const { model } = this.props;
        const { width, height, x, y } = model;
        const position = {
            x: x - width / 2,
            y: y - height / 2,
        }
        const style = model.getNodeStyle();
        return h('rect', { ...style, ...position });
    }
}
class TaskNodeModel extends RectNodeModel {
    /**
     * 初始化 NodeTask 实例，保存依赖并建立组件所需的初始状态。
     * @param data - 本次操作使用的输入数据。
     * @param graphModel - graphModel 对应的业务输入；具体类型和空值范围以方法签名为准。
     */
    constructor(data, graphModel) {
        super(data, graphModel);
        this.radius = 20;
    }
}
export default {
    type: 'task-node',
    view: TaskNode,
    model: TaskNodeModel,
}