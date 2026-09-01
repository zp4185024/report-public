// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 枚举转字典
/// </summary>
[JobDetail("job_EnumToDictJob", Description = "枚举转字典", GroupName = "default", Concurrent = false)]
[PeriodSeconds(1, TriggerId = "trigger_EnumToDictJob", Description = "枚举转字典", MaxNumberOfRuns = 1, RunOnStart = true)]
public class EnumToDictJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private const string DefaultTagType = null;
    private const int OrderOffset = 10;

    /// <summary>初始化 EnumToDictJob 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="scopeFactory">当前方法执行时使用的 scopeFactory 输入值。</param>
    public EnumToDictJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>执行 EnumToDictJob 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stoppingToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        var originColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"【{DateTime.Now}】系统枚举转换字典");

        using var serviceScope = _scopeFactory.CreateScope();
        // 第一阶段：构造 ExecuteAsync 当前步骤需要的中间数据和执行参数。
        var db = serviceScope.ServiceProvider.GetRequiredService<ISqlSugarClient>().CopyNew();

        var sysEnumService = serviceScope.ServiceProvider.GetRequiredService<SysEnumService>();
        var sysDictTypeList = GetDictByEnumType(sysEnumService.GetEnumTypeList());

        // 校验枚举类命名规范，字典相关功能中需要通过后缀判断是否为枚举类型
        Console.ForegroundColor = ConsoleColor.Red;
        foreach (var dictType in sysDictTypeList.Where(x => !x.Code.EndsWith("Enum")))
            Console.WriteLine($"【{DateTime.Now}】系统枚举转换字典的枚举类名称必须以Enum结尾: {dictType.Code} ({dictType.Name})");
        sysDictTypeList = sysDictTypeList.Where(x => x.Code.EndsWith("Enum")).ToList();

        await SyncEnumToDictInfoAsync(db, sysDictTypeList);

        Console.ForegroundColor = ConsoleColor.Yellow;
        try
        {
            await db.BeginTranAsync();
            var storageable1 = await db.Storageable(sysDictTypeList)
                .SplitUpdate(it => it.Any())
                .SplitInsert(_ => true)
                .ToStorageAsync();
            await storageable1.AsInsertable.ExecuteCommandAsync(stoppingToken);
            await storageable1.AsUpdateable.ExecuteCommandAsync(stoppingToken);

            Console.WriteLine($"【{DateTime.Now}】系统枚举类转字典类型数据: 插入{storageable1.InsertList.Count}条, 更新{storageable1.UpdateList.Count}条, 共{storageable1.TotalList.Count}条。");

            var storageable2 = await db.Storageable(sysDictTypeList.SelectMany(x => x.Children).ToList())
                .WhereColumns(it => new { it.DictTypeId, it.Value })
                .SplitUpdate(it => it.Any())
                .SplitInsert(_ => true)
                .ToStorageAsync();
            // 第2阶段：调用 ExecuteAsync 依赖的异步能力，等待并校验返回数据。
            await storageable2.AsInsertable.ExecuteCommandAsync(stoppingToken);
            await storageable2.AsUpdateable.UpdateColumns(u => new
            {
                u.Label,
                u.Code,
                u.Value
            }).ExecuteCommandAsync(stoppingToken);

            Console.WriteLine($"【{DateTime.Now}】系统枚举项转字典值数据: 插入{storageable2.InsertList.Count}条, 更新{storageable2.UpdateList.Count}条, 共{storageable2.TotalList.Count}条。");

            await db.CommitTranAsync();
        }
        catch (Exception error)
        {
            await db.RollbackTranAsync();
            Log.Error($"系统枚举转换字典操作错误：{error.Message}\n堆栈跟踪：{error.StackTrace}", error);
            throw;
        }
        finally
        {
            Console.ForegroundColor = originColor;
        }
    }

    /// <summary>
    /// 用于同步枚举转字典数据
    /// </summary>
    /// <param name="db">当前方法执行时使用的 db 输入值。</param>
    /// <param name="list">需要遍历、筛选或写入的业务数据集合。</param>
    private async Task SyncEnumToDictInfoAsync(SqlSugarClient db, List<SysDictType> list)
    {
        var codeList = list.Select(x => x.Code).ToList();
        foreach (var dbDictType in await db.Queryable<SysDictType>().ClearFilter().Where(x => codeList.Contains(x.Code)).ToListAsync() ?? new())
        {
            var enumDictType = list.First(x => x.Code == dbDictType.Code);
            if (enumDictType.Id == dbDictType.Id)
            {
                // 字典值表字段改变后每条字典值记录会多出一条，此处用于删除多余的字典值数据
                var dataValueList = enumDictType.Children.Select(e => e.Value).ToList();
                await db.Deleteable<SysDictData>().Where(x => x.DictTypeId == dbDictType.Id && !dataValueList.Contains(x.Value)).ExecuteCommandAsync();
                continue;
            }

            // 数据不一致则删除
            await db.Deleteable<SysDictData>().Where(x => x.DictTypeId == dbDictType.Id).ExecuteCommandAsync();
            await db.Deleteable<SysDictType>().Where(x => x.Id == dbDictType.Id).ExecuteCommandAsync();
            Console.WriteLine($"【{DateTime.Now}】删除字典数据: {dbDictType.Name}-{dbDictType.Code}");
        }
    }

    /// <summary>查询或读取 EnumToDictJob 所需的 Get Dict By Enum Type 数据，并按调用约定返回结果。</summary>
    /// <param name="enumTypeList">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private List<SysDictType> GetDictByEnumType(List<EnumTypeOutput> enumTypeList)
    {
        var orderNo = 1;
        var list = new List<SysDictType>();
        foreach (var type in enumTypeList)
        {
            var dictType = new SysDictType
            {
                Id = 900000000000 + CommonUtil.GetFixedHashCode(type.TypeFullName),
                SysFlag = YesNoEnum.Y,
                Code = type.TypeName,
                Name = type.TypeDescribe,
                Remark = type.TypeFullName
            };
            dictType.Children = type.EnumEntities.Select(x => new SysDictData
            {
                Id = dictType.Id + orderNo++,
                DictTypeId = dictType.Id,
                Code = x.Name,
                Label = x.Describe,
                Value = x.Value.ToString(),
                OrderNo = x.Value + OrderOffset,
                TagType = x.Theme != "" ? x.Theme : DefaultTagType
            }).ToList();
            list.Add(dictType);
        }
        return list;
    }
}