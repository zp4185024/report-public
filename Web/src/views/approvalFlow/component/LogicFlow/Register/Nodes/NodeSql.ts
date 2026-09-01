import { HtmlNode, HtmlNodeModel, h } from '@logicflow/core';

class SqlNode extends HtmlNode {
    /**
     * 1.1.7版本后支持在view中重写锚点形状。
     * 重写锚点新增
     */
    getAnchorShape(anchorData) {
        const { x, y, type } = anchorData;
        return h('rect', {
            x: x - 5,
            y: y - 5,
            width: 10,
            height: 10,
            className: `custom-anchor ${type === 'left' ? 'incomming-anchor' : 'outgoing-anchor'
                }`,
        });
    }
    /**
     * 更新 NodeSql 的 set Html 状态，并同步相关业务数据。
     * @param rootEl - rootEl 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    setHtml(rootEl) {
        rootEl.innerHTML = '';
        const {
            properties: { fields, tableName },
        } = this.props.model;
        rootEl.setAttribute('class', 'table-container');
        const container = document.createElement('div');
        container.className = `table-node table-color-${Math.ceil(
            Math.random() * 4,
        )}`;
        const tableNameElement = document.createElement('div');
        tableNameElement.innerText = tableName;
        tableNameElement.className = 'table-name';
        container.appendChild(tableNameElement);
        const fragment = document.createDocumentFragment();
        for (let i = 0; i < fields.length; i++) {
            const item = fields[i];
            const itemElement = document.createElement('div');
            itemElement.className = 'table-feild';
            const itemKey = document.createElement('span');
            itemKey.innerText = item.key;
            const itemType = document.createElement('span');
            itemType.innerText = item.type;
            itemType.className = 'feild-type';
            itemElement.appendChild(itemKey);
            itemElement.appendChild(itemType);
            fragment.appendChild(itemElement);
        }
        container.appendChild(fragment);
        rootEl.appendChild(container);
    }
}

class SqlNodeModel extends HtmlNodeModel {
    /**
     * 给model自定义添加字段方法
     */
    addField(item) {
        this.properties.fields.unshift(item);
        this.setAttributes();
        // 为了保持节点顶部位置不变，在节点变化后，对节点进行一个位移,位移距离为添加高度的一半。
        this.move(0, 24 / 2);
        // 更新节点连接边的path
        this.incoming.edges.forEach((egde) => {
            // 调用自定义的更新方案
            egde.updatePathByAnchor();
        });
        this.outgoing.edges.forEach((edge) => {
            // 调用自定义的更新方案
            edge.updatePathByAnchor();
        });
    }
    /**
     * 查询或读取 NodeSql 所需的 get Outline Style 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getOutlineStyle() {
        const style = super.getOutlineStyle();
        style.stroke = 'none';
        style.hover.stroke = 'none';
        return style;
    }
    // 如果不用修改锚地形状，可以重写颜色相关样式
    /**
     * 查询或读取 NodeSql 所需的 get Anchor Style 数据，并按调用约定返回结果。
     * @param anchorInfo - anchorInfo 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getAnchorStyle(anchorInfo) {
        const style = super.getAnchorStyle();
        if (anchorInfo.type === 'left') {
            style.fill = 'red';
            style.hover.fill = 'transparent';
            style.hover.stroke = 'transpanrent';
            style.className = 'lf-hide-default';
        } else {
            style.fill = 'green';
        }
        return style;
    }
    /**
     * 更新 NodeSql 的 set Attributes 状态，并同步相关业务数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    setAttributes() {
        this.width = 200;
        const {
            properties: { fields },
        } = this;
        this.height = 60 + fields.length * 24;
        const circleOnlyAsTarget = {
            message: '只允许从右边的锚点连出',
            /**
             * 校验 NodeSql 的 validate 条件，并返回可供后续流程判断的结果。
             * @param sourceNode - sourceNode 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @param targetNode - targetNode 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @param sourceAnchor - sourceAnchor 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @returns 返回值与当前方法声明的类型及空值约束保持一致。
             */
            validate: (sourceNode, targetNode, sourceAnchor) => {
                return sourceAnchor.type === 'right';
            },
        };
        this.sourceRules.push(circleOnlyAsTarget);
        this.targetRules.push({
            message: '只允许连接左边的锚点',
            /**
             * 校验 NodeSql 的 validate 条件，并返回可供后续流程判断的结果。
             * @param sourceNode - sourceNode 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @param targetNode - targetNode 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @param sourceAnchor - sourceAnchor 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @param targetAnchor - targetAnchor 对应的业务输入；具体类型和空值范围以方法签名为准。
             * @returns 返回值与当前方法声明的类型及空值约束保持一致。
             */
            validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
                return targetAnchor.type === 'left';
            },
        });
    }
    /**
     * 查询或读取 NodeSql 所需的 get Default Anchor 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    getDefaultAnchor() {
        const {
            id,
            x,
            y,
            width,
            height,
            isHovered,
            isSelected,
            properties: { fields, isConnection },
        } = this;
        const anchors = [];
        fields.forEach((feild, index) => {
            // 如果是连出，就不显示左边的锚点
            if (isConnection || !(isHovered || isSelected)) {
                anchors.push({
                    x: x - width / 2 + 10,
                    y: y - height / 2 + 60 + index * 24,
                    id: `${id}_${feild.key}_left`,
                    edgeAddable: false,
                    type: 'left',
                });
            }
            if (!isConnection) {
                anchors.push({
                    x: x + width / 2 - 10,
                    y: y - height / 2 + 60 + index * 24,
                    id: `${id}_${feild.key}_right`,
                    type: 'right',
                });
            }
        });
        return anchors;
    }
}

export default {
    type: 'sql-node',
    model: SqlNodeModel,
    view: SqlNode,
};