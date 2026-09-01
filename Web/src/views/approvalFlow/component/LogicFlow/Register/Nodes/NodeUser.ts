import { PolygonNode, PolygonNodeModel, h } from "@logicflow/core";

class UserNode extends PolygonNode {
    /**
     * 查询或读取 NodeUser 所需的 get Icon Shape 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getIconShape() {
        const { model } = this.props
        const { stroke } = model.getNodeStyle()
        return h(
            'svg',
            {
                x: 20,
                y: 18,
                width: 30,
                height: 30,
                viewBox: '0 0 1126 1024'
            },
            h(
                'path',
                {
                    fill: stroke,
                    d: 'M792.576 379.392a25.6 25.6 0 0 0 25.2928 25.8048h283.2384A25.6 25.6 0 0 0 1126.4 379.392a25.6 25.6 0 0 0-25.2928-25.8048h-283.2384a25.6 25.6 0 0 0-25.344 25.8048z m303.9232 80.7424H761.856c-16.5376 0-29.9008 11.6224-29.9008 25.7536 0 14.1824 13.312 25.7536 29.9008 25.7536h334.6432c16.4864 0 29.9008-11.5712 29.9008-25.7536 0-14.1312-13.4144-25.7536-29.9008-25.7536z m4.608 106.496h-283.2384a25.6 25.6 0 0 0-25.344 25.7536 25.6 25.6 0 0 0 25.344 25.7536h283.2384A25.6 25.6 0 0 0 1126.4 592.384a25.6 25.6 0 0 0-25.2928-25.8048zM543.0272 1024H341.6576C150.8352 1024 0 1024 0 923.648v-20.1216c0-188.16 153.2928-341.1968 341.7088-341.1968h201.2672c188.416 0 341.76 153.0368 341.76 341.1968v20.0704C884.6848 1024 726.3232 1024 542.976 1024z m-203.1616-405.1456c-158.464 0-287.4368 128.4096-287.4368 286.208v20.48c0 40.9088 166.0928 40.9088 287.4368 40.9088h204.9536c100.4544 0 287.4368 0 287.4368-40.96v-20.3776c0-157.8496-128.9728-286.208-287.4368-286.208H339.8656z m92.416-76.7488a271.4112 271.4112 0 0 1-271.2064-271.0528A271.36 271.36 0 0 1 432.2816 0a271.36 271.36 0 0 1 271.2064 271.0528 271.4624 271.4624 0 0 1-271.2064 271.0528z m-215.3472-271.872c0 118.1696 96.6144 214.3232 215.3472 214.3232 118.784 0 215.3984-96.1536 215.3984-214.3232 0-118.2208-96.6144-214.3232-215.3984-214.3232S216.9344 152.0128 216.9344 270.2336z'
                }
            )
        )
    }
    /**
     * 查询或读取 NodeUser 所需的 get Shape 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getShape() {
        const { model } = this.props
        const { width, height, x, y, points } = model
        const { fill, fillOpacity, strokeWidth, stroke, strokeOpacity, } = model.getNodeStyle()
        const transform = `matrix(1 0 0 1 ${x - width / 2} ${y - height / 2})`
        const pointsPath = points.map(point => point.join(',')).join(' ')
        return h(
            'g',
            {
                transform
            },
            [
                h(
                    'polygon',
                    {
                        points: pointsPath,
                        fill,
                        stroke,
                        strokeWidth,
                        strokeOpacity,
                        fillOpacity
                    }
                ),
                this.getIconShape()
            ]
        )
    }
}
class UserNodeModel extends PolygonNodeModel {
    /**
     * 初始化 NodeUser 实例，保存依赖并建立组件所需的初始状态。
     * @param data - 本次操作使用的输入数据。
     * @param graphModel - graphModel 对应的业务输入；具体类型和空值范围以方法签名为准。
     */
    constructor(data, graphModel) {
        data.text = {
            value: (data.text && data.text.value) || '',
            x: data.x,
            y: data.y + 50
        }
        super(data, graphModel)
        // 右键菜单自由配置，也可以通过边的properties或者其他属性条件更换不同菜单
        this.menu = [
            {
                className: 'lf-menu-delete',
                text: 'delete',
                /**
                 * 接收 NodeUser 的异步处理结果并同步调用方关心的状态。
                 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
                 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
                 */
                callback(node) {
                    // const comfirm = window.confirm('你确定要删除吗？')
                    lf.deleteNode(node.id)
                }
            },
            {
                text: 'edit',
                className: 'lf-menu-item',
                /**
                 * 接收 NodeUser 的异步处理结果并同步调用方关心的状态。
                 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
                 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
                 */
                callback(node) {
                    lf.editText(node.id)
                }
            },
            {
                text: 'copy',
                className: 'lf-menu-item',
                /**
                 * 接收 NodeUser 的异步处理结果并同步调用方关心的状态。
                 * @param node - node 对应的业务输入；具体类型和空值范围以方法签名为准。
                 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
                 */
                callback(node) {
                    lf.cloneNode(node.id)
                }
            }
        ]
    }
    /**
     * 初始化 NodeUser 的 init Node Data 状态，创建依赖并注册运行期间需要的资源。
     * @param data - 本次操作使用的输入数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    initNodeData(data) {
        super.initNodeData(data)
        const lenght = 35
        this.points = [
            [lenght, 0],
            [lenght * 2, lenght],
            [lenght, lenght * 2],
            [0, lenght]
        ]
    }
    // 自定义锚点样式
    /**
     * 查询或读取 NodeUser 所需的 get Anchor Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getAnchorStyle() {
        const style = super.getAnchorStyle();
        style.hover.r = 8;
        style.hover.fill = "rgb(24, 125, 255)";
        style.hover.stroke = "rgb(24, 125, 255)";
        return style;
    }
}
export default {
    type: 'user-node',
    view: UserNode,
    model: UserNodeModel
}