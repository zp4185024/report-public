// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 树形节点
/// </summary>
public class TreeNode
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public string Name { get; set; }
    public List<TreeNode> Children { get; set; } = new();
}

/// <summary>
/// 根据路径数组生成树结构
/// </summary>
public class PathTreeBuilder
{
    private int _nextId = 1;

    public TreeNode BuildTree(List<string> paths)
    {
        var root = new TreeNode { Id = 1, Pid = 0, Name = "文件目录" }; // 根节点
        var dict = new Dictionary<string, TreeNode>();

        foreach (var path in paths)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            TreeNode currentNode = root;

            foreach (var part in parts)
            {
                var key = currentNode.Id + "_" + part; // 生成唯一键
                if (!dict.ContainsKey(key))
                {
                    var newNode = new TreeNode
                    {
                        Id = _nextId++,
                        Pid = currentNode.Id,
                        Name = part
                    };
                    currentNode.Children.Add(newNode);
                    dict[key] = newNode;
                }
                currentNode = dict[key]; // 更新当前节点
            }
        }

        return root;
    }
}