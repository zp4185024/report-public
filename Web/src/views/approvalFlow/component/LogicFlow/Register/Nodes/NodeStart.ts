import { CircleNode, CircleNodeModel, h } from "@logicflow/core";

class StartNode extends CircleNode {
    /**
     * 查询或读取 NodeStart 所需的 get Label Shape 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getLabelShape() {
        const { model } = this.props;
        const { x, y } = model;
        return h('text', { fill: '#000000', fontSize: 12, x: x - 12, y: y + 4, width: 50, height: 25 }, 'Start');
    }
    /**
     * 查询或读取 NodeStart 所需的 get Shape 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getShape() {
        const { model } = this.props;
        const { x, y, r } = model;
        const { fill, stroke, strokeWidth } = model.getNodeStyle();
        return h('g', {}, [h('circle', { cx: x, cy: y, r, fill, stroke, strokeWidth }), this.getLabelShape()]);
    }
}

class StartModel extends CircleNodeModel {
    // 自定义节点形状属性
    /**
     * 初始化 NodeStart 的 init Node Data 状态，创建依赖并注册运行期间需要的资源。
     * @param data - 本次操作使用的输入数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    initNodeData(data) {
        data.text = {
            value: (data.text && data.text.value) || '',
            x: data.x,
            y: data.y + 35,
            dragable: false,
            editable: true
        }
        super.initNodeData(data)
        this.r = 20
    }
    // 自定义节点样式属性
    /**
     * 查询或读取 NodeStart 所需的 get Node Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getNodeStyle() {
        const style = super.getNodeStyle()
        return style
    }
    // 自定义锚点样式
    /**
     * 查询或读取 NodeStart 所需的 get Anchor Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getAnchorStyle() {
        const style = super.getAnchorStyle()
        style.hover.r = 8
        style.hover.fill = 'rgb(24, 125, 255)'
        style.hover.stroke = 'rgb(24, 125, 255)'
        return style
    }
    // 自定义节点outline
    /**
     * 查询或读取 NodeStart 所需的 get Outline Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getOutlineStyle() {
        const style = super.getOutlineStyle()
        style.stroke = '#88f'
        return style
    }
    /**
     * 查询或读取 NodeStart 所需的 get Connected Target Rules 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getConnectedTargetRules() {
        const rules = super.getConnectedTargetRules()
        const notAsTarget = {
            message: '起始节点不能作为连线的终点',
            /**
             * 校验 NodeStart 的 validate 条件，并返回可供后续流程判断的结果。
             * @returns 返回值与当前方法声明的类型及空值约束保持一致。
             */
            validate: () => false
        }
        rules.push(notAsTarget)
        return rules
    }
}

export default {
    type: 'start-node',
    view: StartNode,
    model: StartModel
}