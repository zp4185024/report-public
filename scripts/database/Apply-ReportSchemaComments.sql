/*
   为报表中心的 Rc* 表补齐 SQL Server 表说明和字段说明。
   脚本可重复执行：已有 MS_Description 会更新，不存在时新增；不会修改表结构和数据。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @Comments TABLE
(
    TableName sysname NOT NULL,
    ColumnName sysname NULL,
    Description nvarchar(4000) NOT NULL
);

INSERT INTO @Comments (TableName, ColumnName, Description)
VALUES
    (N'RcReport', NULL, N'报表定义表'),
    (N'RcReport', N'Id', N'主键Id'),
    (N'RcReport', N'Code', N'报表编码'),
    (N'RcReport', N'Name', N'报表名称'),
    (N'RcReport', N'SheetName', N'工作表名称'),
    (N'RcReport', N'DatasetCode', N'数据集编码'),
    (N'RcReport', N'PreviewSettingsJson', N'预览设置'),
    (N'RcReport', N'ExportSettingsJson', N'导出设置'),
    (N'RcReport', N'FieldPresentationsJson', N'返回字段显示映射'),
    (N'RcReport', N'Revision', N'配置修订号'),
    (N'RcReport', N'IsEnabled', N'是否启用'),
    (N'RcReport', N'CreateTime', N'创建时间'),
    (N'RcReport', N'UpdateTime', N'更新时间'),
    (N'RcReport', N'CreateUserId', N'创建者Id'),
    (N'RcReport', N'CreateUserName', N'创建者姓名'),
    (N'RcReport', N'UpdateUserId', N'修改者Id'),
    (N'RcReport', N'UpdateUserName', N'修改者姓名'),

    (N'RcDataSource', NULL, N'报表数据源表'),
    (N'RcDataSource', N'Id', N'主键Id'),
    (N'RcDataSource', N'Code', N'数据源编码'),
    (N'RcDataSource', N'Name', N'数据源名称'),
    (N'RcDataSource', N'DbType', N'数据库类型'),
    (N'RcDataSource', N'ConnectionString', N'连接字符串'),
    (N'RcDataSource', N'DefaultTimeoutSeconds', N'默认查询超时秒数'),
    (N'RcDataSource', N'DefaultMaxRows', N'默认最大返回行数'),
    (N'RcDataSource', N'Revision', N'配置修订号'),
    (N'RcDataSource', N'IsEnabled', N'是否启用'),
    (N'RcDataSource', N'CreateTime', N'创建时间'),
    (N'RcDataSource', N'UpdateTime', N'更新时间'),
    (N'RcDataSource', N'CreateUserId', N'创建者Id'),
    (N'RcDataSource', N'CreateUserName', N'创建者姓名'),
    (N'RcDataSource', N'UpdateUserId', N'修改者Id'),
    (N'RcDataSource', N'UpdateUserName', N'修改者姓名'),

    (N'RcDataset', NULL, N'报表数据集表'),
    (N'RcDataset', N'Id', N'主键Id'),
    (N'RcDataset', N'DataSourceCode', N'数据源编码'),
    (N'RcDataset', N'Code', N'数据集编码'),
    (N'RcDataset', N'Name', N'数据集名称'),
    (N'RcDataset', N'QueryText', N'只读参数化SQL'),
    (N'RcDataset', N'FieldSchemaJson', N'数据集输入变量和返回字段契约'),
    (N'RcDataset', N'TimeoutSeconds', N'查询超时秒数'),
    (N'RcDataset', N'MaxRows', N'最大返回行数'),
    (N'RcDataset', N'Revision', N'配置修订号'),
    (N'RcDataset', N'IsEnabled', N'是否启用'),
    (N'RcDataset', N'CreateTime', N'创建时间'),
    (N'RcDataset', N'UpdateTime', N'更新时间'),
    (N'RcDataset', N'CreateUserId', N'创建者Id'),
    (N'RcDataset', N'CreateUserName', N'创建者姓名'),
    (N'RcDataset', N'UpdateUserId', N'修改者Id'),
    (N'RcDataset', N'UpdateUserName', N'修改者姓名'),

    (N'RcReportParameter', NULL, N'报表参数表'),
    (N'RcReportParameter', N'Id', N'主键Id'),
    (N'RcReportParameter', N'ReportCode', N'报表编码'),
    (N'RcReportParameter', N'Code', N'参数编码'),
    (N'RcReportParameter', N'DisplayName', N'显示名称'),
    (N'RcReportParameter', N'DataType', N'数据类型'),
    (N'RcReportParameter', N'ControlType', N'控件类型'),
    (N'RcReportParameter', N'SourceType', N'来源类型'),
    (N'RcReportParameter', N'SourceCode', N'外部参数源编码'),
    (N'RcReportParameter', N'SourceFieldCode', N'外部返回字段编码'),
    (N'RcReportParameter', N'UrlKey', N'URL参数名'),
    (N'RcReportParameter', N'DefaultExpression', N'默认表达式'),
    (N'RcReportParameter', N'FixedValue', N'固定值'),
    (N'RcReportParameter', N'OptionsJson', N'参数可选项'),
    (N'RcReportParameter', N'IsVisible', N'是否显示'),
    (N'RcReportParameter', N'IsReadOnly', N'是否只读'),
    (N'RcReportParameter', N'IsRequired', N'是否必填'),
    (N'RcReportParameter', N'AllowUrlPrefill', N'是否允许URL预填'),
    (N'RcReportParameter', N'DisplayOrder', N'显示顺序'),
    (N'RcReportParameter', N'CreateTime', N'创建时间'),
    (N'RcReportParameter', N'UpdateTime', N'更新时间'),
    (N'RcReportParameter', N'CreateUserId', N'创建者Id'),
    (N'RcReportParameter', N'CreateUserName', N'创建者姓名'),
    (N'RcReportParameter', N'UpdateUserId', N'修改者Id'),
    (N'RcReportParameter', N'UpdateUserName', N'修改者姓名'),

    (N'RcExternalParameterSource', NULL, N'外部参数接口源表'),
    (N'RcExternalParameterSource', N'Id', N'主键Id'),
    (N'RcExternalParameterSource', N'Code', N'接口源编码'),
    (N'RcExternalParameterSource', N'Name', N'接口源名称'),
    (N'RcExternalParameterSource', N'BaseUrl', N'基础地址'),
    (N'RcExternalParameterSource', N'Endpoint', N'接口路径'),
    (N'RcExternalParameterSource', N'HttpMethod', N'HTTP 方法'),
    (N'RcExternalParameterSource', N'RequestBodyTemplate', N'请求体模板'),
    (N'RcExternalParameterSource', N'HeadersTemplate', N'请求头模板'),
    (N'RcExternalParameterSource', N'RequestParameterSchemaJson', N'URL动态请求参数'),
    (N'RcExternalParameterSource', N'FieldSchemaJson', N'响应字段映射'),
    (N'RcExternalParameterSource', N'TimeoutSeconds', N'请求超时秒数'),
    (N'RcExternalParameterSource', N'Revision', N'配置修订号'),
    (N'RcExternalParameterSource', N'IsEnabled', N'是否启用'),
    (N'RcExternalParameterSource', N'CreateTime', N'创建时间'),
    (N'RcExternalParameterSource', N'UpdateTime', N'更新时间'),
    (N'RcExternalParameterSource', N'CreateUserId', N'创建者Id'),
    (N'RcExternalParameterSource', N'CreateUserName', N'创建者姓名'),
    (N'RcExternalParameterSource', N'UpdateUserId', N'修改者Id'),
    (N'RcExternalParameterSource', N'UpdateUserName', N'修改者姓名'),

    (N'RcReportTemplate', NULL, N'报表中心模板表'),
    (N'RcReportTemplate', N'Id', N'主键Id'),
    (N'RcReportTemplate', N'ReportCode', N'报表编码'),
    (N'RcReportTemplate', N'SnapshotJson', N'Univer工作簿快照'),
    (N'RcReportTemplate', N'BindingJson', N'模板绑定'),
    (N'RcReportTemplate', N'Revision', N'修订号'),
    (N'RcReportTemplate', N'CreateTime', N'创建时间'),
    (N'RcReportTemplate', N'UpdateTime', N'更新时间'),
    (N'RcReportTemplate', N'CreateUserId', N'创建者Id'),
    (N'RcReportTemplate', N'CreateUserName', N'创建者姓名'),
    (N'RcReportTemplate', N'UpdateUserId', N'修改者Id'),
    (N'RcReportTemplate', N'UpdateUserName', N'修改者姓名'),

    (N'RcExchangeMapping', NULL, N'报表中心Excel与Univer映射表'),
    (N'RcExchangeMapping', N'Id', N'主键Id'),
    (N'RcExchangeMapping', N'UniverVersion', N'Univer版本'),
    (N'RcExchangeMapping', N'MappingVersion', N'映射版本'),
    (N'RcExchangeMapping', N'RuleCode', N'规则编码'),
    (N'RcExchangeMapping', N'Direction', N'转换方向'),
    (N'RcExchangeMapping', N'Category', N'规则分类'),
    (N'RcExchangeMapping', N'SourcePath', N'来源路径'),
    (N'RcExchangeMapping', N'TargetPath', N'目标路径'),
    (N'RcExchangeMapping', N'TransformType', N'转换类型'),
    (N'RcExchangeMapping', N'SourceValue', N'来源枚举值'),
    (N'RcExchangeMapping', N'TargetValue', N'目标枚举值'),
    (N'RcExchangeMapping', N'Factor', N'数值换算系数'),
    (N'RcExchangeMapping', N'IsRequired', N'是否必要'),
    (N'RcExchangeMapping', N'IsEnabled', N'是否启用'),
    (N'RcExchangeMapping', N'Sort', N'执行顺序'),
    (N'RcExchangeMapping', N'Remark', N'调试说明'),
    (N'RcExchangeMapping', N'CreateTime', N'创建时间'),
    (N'RcExchangeMapping', N'UpdateTime', N'更新时间'),
    (N'RcExchangeMapping', N'CreateUserId', N'创建者Id'),
    (N'RcExchangeMapping', N'CreateUserName', N'创建者姓名'),
    (N'RcExchangeMapping', N'UpdateUserId', N'修改者Id'),
    (N'RcExchangeMapping', N'UpdateUserName', N'修改者姓名');

DECLARE @TableName sysname;
DECLARE @ColumnName sysname;
DECLARE @Description nvarchar(4000);
DECLARE @ObjectId int;
DECLARE @ColumnId int;
DECLARE comment_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName, ColumnName, Description FROM @Comments;

OPEN comment_cursor;
FETCH NEXT FROM comment_cursor INTO @TableName, @ColumnName, @Description;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @ObjectId = OBJECT_ID(N'dbo.' + @TableName, N'U');
    IF @ObjectId IS NOT NULL
    BEGIN
        IF @ColumnName IS NULL
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM sys.extended_properties
                WHERE class = 1 AND major_id = @ObjectId AND minor_id = 0 AND name = N'MS_Description')
            BEGIN
                EXEC sys.sp_updateextendedproperty
                    @name = N'MS_Description', @value = @Description,
                    @level0type = N'SCHEMA', @level0name = N'dbo',
                    @level1type = N'TABLE', @level1name = @TableName;
            END
            ELSE
            BEGIN
                EXEC sys.sp_addextendedproperty
                    @name = N'MS_Description', @value = @Description,
                    @level0type = N'SCHEMA', @level0name = N'dbo',
                    @level1type = N'TABLE', @level1name = @TableName;
            END
        END
        ELSE
        BEGIN
            SET @ColumnId = COLUMNPROPERTY(@ObjectId, @ColumnName, 'ColumnId');
            IF @ColumnId IS NOT NULL
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM sys.extended_properties
                    WHERE class = 1 AND major_id = @ObjectId AND minor_id = @ColumnId AND name = N'MS_Description')
                BEGIN
                    EXEC sys.sp_updateextendedproperty
                        @name = N'MS_Description', @value = @Description,
                        @level0type = N'SCHEMA', @level0name = N'dbo',
                        @level1type = N'TABLE', @level1name = @TableName,
                        @level2type = N'COLUMN', @level2name = @ColumnName;
                END
                ELSE
                BEGIN
                    EXEC sys.sp_addextendedproperty
                        @name = N'MS_Description', @value = @Description,
                        @level0type = N'SCHEMA', @level0name = N'dbo',
                        @level1type = N'TABLE', @level1name = @TableName,
                        @level2type = N'COLUMN', @level2name = @ColumnName;
                END
            END
        END
    END

    FETCH NEXT FROM comment_cursor INTO @TableName, @ColumnName, @Description;
END

CLOSE comment_cursor;
DEALLOCATE comment_cursor;
COMMIT TRANSACTION;

SELECT
    t.name AS TableName,
    ep.value AS TableDescription
FROM sys.tables t
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND t.name LIKE N'Rc%'
ORDER BY t.name;

SELECT
    t.name AS TableName,
    c.name AS ColumnName,
    ep.value AS ColumnDescription
FROM sys.tables t
INNER JOIN sys.columns c ON c.object_id = t.object_id
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1
   AND ep.major_id = c.object_id
   AND ep.minor_id = c.column_id
   AND ep.name = N'MS_Description'
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND t.name LIKE N'Rc%'
ORDER BY t.name, c.column_id;
