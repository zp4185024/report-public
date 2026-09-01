import { BezierEdge, BezierEdgeModel } from '@logicflow/core';

class CustomEdge2 extends BezierEdge { }

class CustomEdgeModel2 extends BezierEdgeModel {
    /**
     * 查询或读取 EdgeSql 所需的 get Edge Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getEdgeStyle() {
        const style = super.getEdgeStyle();
        // svg属性
        style.strokeWidth = 1;
        style.stroke = '#ababac';
        return style;
    }
    /**
     * 重写此方法，使保存数据是能带上锚点数据。
     */
    getData() {
        const data = super.getData();
        data.sourceAnchorId = this.sourceAnchorId;
        data.targetAnchorId = this.targetAnchorId;
        return data;
    }
    /**
     * 给边自定义方案，使其支持基于锚点的位置更新边的路径
     */
    updatePathByAnchor() {
        // TODO
        const sourceNodeModel = this.graphModel.getNodeModelById(this.sourceNodeId);
        const sourceAnchor = sourceNodeModel
            .getDefaultAnchor()
            .find((anchor) => anchor.id === this.sourceAnchorId);
        const targetNodeModel = this.graphModel.getNodeModelById(this.targetNodeId);
        const targetAnchor = targetNodeModel
            .getDefaultAnchor()
            .find((anchor) => anchor.id === this.targetAnchorId);
        const startPoint = {
            x: sourceAnchor.x,
            y: sourceAnchor.y,
        };
        this.updateStartPoint(startPoint);
        const endPoint = {
            x: targetAnchor.x,
            y: targetAnchor.y,
        };
        this.updateEndPoint(endPoint);
        // 这里需要将原有的pointsList设置为空，才能触发bezier的自动计算control点。
        this.pointsList = [];
        this.initPoints();
    }
}

export default {
    type: 'edge-sql',
    view: CustomEdge2,
    model: CustomEdgeModel2,
};