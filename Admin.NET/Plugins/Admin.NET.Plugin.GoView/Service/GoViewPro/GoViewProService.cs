// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.GoView.Service;

/// <summary>
/// 项目管理服务 🧩
/// </summary>
[UnifyProvider("GoView")]
[ApiDescriptionSettings(GoViewConst.GroupName, Module = "goview", Name = "project", Order = 100, Description = "项目管理")]
public class GoViewProService : IDynamicApiController
{
    private readonly SqlSugarRepository<GoViewPro> _goViewProRep;
    private readonly SqlSugarRepository<GoViewProData> _goViewProDataRep;

    /// <summary>初始化 GoViewProService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="goViewProjectRep">goViewProjectRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="goViewProjectDataRep">goViewProjectDataRep 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public GoViewProService(SqlSugarRepository<GoViewPro> goViewProjectRep,
        SqlSugarRepository<GoViewProData> goViewProjectDataRep)
    {
        _goViewProRep = goViewProjectRep;
        _goViewProDataRep = goViewProjectDataRep;
    }

    /// <summary>查询或读取 GoViewProService 所需的 Get List 数据，并按调用约定返回结果。</summary>
    /// <param name="page">控制数据范围或位置的数值。</param>
    /// <param name="limit">控制数据范围或位置的数值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("获取项目列表")]
    public async Task<List<GoViewProItemOutput>> GetList([FromQuery] int page = 1, [FromQuery] int limit = 12)
    {
        var res = await _goViewProRep.AsQueryable()
            .Select(u => new GoViewProItemOutput(), true)
            .ToPagedListAsync(page, limit);
        return res.Items.ToList();
    }

    /// <summary>构建 GoViewProService 所需的 Create 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [ApiDescriptionSettings(Name = "Create")]
    [DisplayName("新增项目")]
    public async Task<GoViewProCreateOutput> Create(GoViewProCreateInput input)
    {
        var project = await _goViewProRep.AsInsertable(input.Adapt<GoViewPro>()).ExecuteReturnEntityAsync();
        return new GoViewProCreateOutput
        {
            Id = project.Id
        };
    }

    /// <summary>更新 GoViewProService 的 Edit 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [DisplayName("修改项目")]
    public async Task Edit(GoViewProEditInput input)
    {
        await _goViewProRep.AsUpdateable(input.Adapt<GoViewPro>()).IgnoreColumns(true).ExecuteCommandAsync();
    }

    /// <summary>移除 GoViewProService 中与 Delete 对应的数据，并清理关联状态。</summary>
    [ApiDescriptionSettings(Name = "Delete")]
    [DisplayName("删除项目")]
    [UnitOfWork]
    public async Task Delete([FromQuery] string ids)
    {
        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(u => Convert.ToInt64(u)).ToList();
        await _goViewProRep.AsDeleteable().Where(u => idList.Contains(u.Id)).ExecuteCommandAsync();
        await _goViewProDataRep.AsDeleteable().Where(u => idList.Contains(u.Id)).ExecuteCommandAsync();
    }

    /// <summary>调用 GoViewProService 的异步依赖完成 Publish 操作，并按当前返回类型传递处理结果。</summary>
    [HttpPut]
    [DisplayName("修改发布状态")]
    public async Task Publish(GoViewProPublishInput input)
    {
        await _goViewProRep.AsUpdateable()
            .SetColumns(u => new GoViewPro
            {
                StateEnum = input.StateEnum
            })
            .Where(u => u.Id == input.Id)
            .ExecuteCommandAsync();
    }

    /// <summary>查询或读取 GoViewProService 所需的 Get Data 数据，并按调用约定返回结果。</summary>
    /// <param name="projectId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [ApiDescriptionSettings(Name = "GetData")]
    [DisplayName("获取项目数据")]
    public async Task<GoViewProDetailOutput> GetData([FromQuery] long projectId)
    {
        var projectData = await _goViewProDataRep.GetByIdAsync(projectId);
        if (projectData == null) return null;

        var project = await _goViewProRep.GetByIdAsync(projectId);
        var projectDetail = project.Adapt<GoViewProDetailOutput>();
        projectDetail.Content = projectData.Content;

        return projectDetail;
    }

    /// <summary>更新 GoViewProService 的 Save Data 状态，并同步相关业务数据。</summary>
    [ApiDescriptionSettings(Name = "save/data")]
    [DisplayName("保存项目数据")]
    public async Task SaveData([FromForm] GoViewProSaveDataInput input)
    {
        if (await _goViewProDataRep.IsAnyAsync(u => u.Id == input.ProjectId))
        {
            await _goViewProDataRep.AsUpdateable()
                .SetColumns(u => new GoViewProData
                {
                    Content = input.Content
                })
                .Where(u => u.Id == input.ProjectId)
                .ExecuteCommandAsync();
        }
        else
        {
            await _goViewProDataRep.InsertAsync(new GoViewProData
            {
                Id = input.ProjectId,
                Content = input.Content,
            });
        }
    }

    /// <summary>接收并校验 GoViewProService 的 Upload 输入，随后写入目标状态。</summary>
    [DisplayName("上传预览图")]
    public async Task<GoViewProUploadOutput> Upload(IFormFile @object)
    {
        /*
         * 前端逻辑（useSync.hook.ts 的 dataSyncUpdate 方法）：
         * 如果 FileUrl 不为空，使用 FileUrl
         * 否则使用 GetOssInfo 接口获取到的 BucketUrl 和 FileName 进行拼接
         */

        // 文件名格式示例 13414795568325_index_preview.png
        var fileNameSplit = @object.FileName.Split('_');
        var idStr = fileNameSplit[0];
        if (!long.TryParse(idStr, out var id)) return new GoViewProUploadOutput();

        // 将预览图转换成 Base64
        var ms = new MemoryStream();
        await @object.CopyToAsync(ms);
        var base64Image = Convert.ToBase64String(ms.ToArray());

        // 保存
        if (await _goViewProDataRep.IsAnyAsync(u => u.Id == id))
        {
            await _goViewProDataRep.AsUpdateable()
                .SetColumns(u => new GoViewProData
                {
                    IndexImageData = base64Image
                })
                .Where(u => u.Id == id)
                .ExecuteCommandAsync();
        }
        else
        {
            await _goViewProDataRep.InsertAsync(new GoViewProData
            {
                Id = id,
                IndexImageData = base64Image,
            });
        }

        var output = new GoViewProUploadOutput
        {
            Id = id,
            BucketName = null,
            CreateTime = null,
            CreateUserId = null,
            FileName = null,
            FileSize = 0,
            FileSuffix = "png",
            FileUrl = $"api/goview/project/getIndexImage/{id}",
            UpdateTime = null,
            UpdateUserId = null
        };

        #region 使用 SysFileService 方式（已注释）

        ////删除已存在的预览图
        //var uploadFileName = Path.GetFileNameWithoutExtension(@object.FileName);
        //var existFiles = await _fileRep.GetListAsync(u => u.FileName == uploadFileName);
        //foreach (var f in existFiles)
        //    await _fileService.DeleteFile(new DeleteFileInput { Id = f.Id });

        ////保存预览图
        //var result = await _fileService.UploadFile(@object, "");
        //var file = await _fileRep.GetByIdAsync(result.Id);
        //int.TryParse(file.SizeKb, out var size);

        ////本地存储，使用拼接的地址
        //var fileUrl = file.BucketName == "Local" ? $"{file.FilePath}/{file.Id}{file.Suffix}" : file.Url;

        //var output = new ProjectUploadOutput
        //{
        //    Id = file.Id,
        //    BucketName = file.BucketName,
        //    CreateTime = file.CreateTime,
        //    CreateUserId = file.CreateUserId,
        //    FileName = $"{file.FileName}{file.Suffix}",
        //    FileSize = size,
        //    FileSuffix = file.Suffix?[1..],
        //    FileUrl = fileUrl,
        //    UpdateTime = null,
        //    UpdateUserId = null
        //};

        #endregion 使用 SysFileService 方式（已注释）

        return output;
    }

    /// <summary>查询或读取 GoViewProService 所需的 Get Index Image 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [NonUnify]
    [ApiDescriptionSettings(Name = "GetIndexImage")]
    [DisplayName("获取预览图")]
    public async Task<IActionResult> GetIndexImage(long id)
    {
        var projectData = await _goViewProDataRep.AsQueryable().IgnoreColumns(u => u.Content).FirstAsync(u => u.Id == id);
        if (projectData?.IndexImageData == null)
            return new NoContentResult();

        var bytes = Convert.FromBase64String(projectData.IndexImageData);
        return new FileStreamResult(new MemoryStream(bytes), "image/png");
    }

    /// <summary>接收并校验 GoViewProService 的 Upload Back Ground 输入，随后写入目标状态。</summary>
    [DisplayName("上传背景图")]
    public async Task<GoViewProUploadOutput> UploadBackGround(IFormFile @object)
    {
        // 文件名格式示例 13414795568325_index_preview.png
        var fileNameSplit = @object.FileName.Split('_');
        var idStr = fileNameSplit[0];
        if (!long.TryParse(idStr, out var id)) return new GoViewProUploadOutput();

        // 将预览图转换成 Base64
        var ms = new MemoryStream();
        await @object.CopyToAsync(ms);
        var base64Image = Convert.ToBase64String(ms.ToArray());

        // 保存
        if (await _goViewProDataRep.IsAnyAsync(u => u.Id == id))
        {
            await _goViewProDataRep.AsUpdateable()
                .SetColumns(u => new GoViewProData
                {
                    BackGroundImageData = base64Image
                })
                .Where(u => u.Id == id)
                .ExecuteCommandAsync();
        }
        else
        {
            await _goViewProDataRep.InsertAsync(new GoViewProData
            {
                Id = id,
                BackGroundImageData = base64Image,
            });
        }

        var output = new GoViewProUploadOutput
        {
            Id = id,
            BucketName = null,
            CreateTime = null,
            CreateUserId = null,
            FileName = null,
            FileSize = 0,
            FileSuffix = "png",
            FileUrl = $"api/goview/project/getBackGroundImage/{id}",
            UpdateTime = null,
            UpdateUserId = null
        };

        return output;
    }

    /// <summary>查询或读取 GoViewProService 所需的 Get Back Ground Image 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [NonUnify]
    [ApiDescriptionSettings(Name = "GetBackGroundImage")]
    [DisplayName("获取背景图")]
    public async Task<IActionResult> GetBackGroundImage(long id)
    {
        var projectData = await _goViewProDataRep.AsQueryable().IgnoreColumns(u => u.Content).FirstAsync(u => u.Id == id);
        if (projectData?.BackGroundImageData == null)
            return new NoContentResult();

        var bytes = Convert.FromBase64String(projectData.BackGroundImageData);
        return new FileStreamResult(new MemoryStream(bytes), "image/png");
    }
}