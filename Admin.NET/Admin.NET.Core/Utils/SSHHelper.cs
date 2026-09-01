// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Renci.SshNet;

namespace Admin.NET.Core
{
    /// <summary>
    /// SSH/Sftp 工具类
    /// </summary>
    public class SSHHelper : IDisposable
    {
        private readonly SftpClient _sftp;

        /// <summary>初始化 SSHHelper 实例，保存后续处理所需的依赖并建立初始状态。</summary>
        /// <param name="host">host 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="port">port 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="user">user 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="password">password 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        public SSHHelper(string host, int port, string user, string password)
        {
            _sftp = new SftpClient(host, port, user, password);
        }

        /// <summary>负责 SSHHelper 的 Connect 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        private void Connect()
        {
            if (!_sftp.IsConnected)
                _sftp.Connect();
        }

        /// <summary>
        /// 是否存在同名文件
        /// </summary>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public bool Exists(string ftpFileName)
        {
            Connect();

            return _sftp.Exists(ftpFileName);
        }

        /// <summary>移除 SSHHelper 中与 Delete File 对应的数据，并清理关联状态。</summary>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        public void DeleteFile(string ftpFileName)
        {
            Connect();

            _sftp.DeleteFile(ftpFileName);
        }

        /// <summary>整理 SSHHelper 的 Download File 数据，并生成可供调用方使用的输出。</summary>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        /// <param name="localFileName">需要读取、校验或转换的业务值。</param>
        public void DownloadFile(string ftpFileName, string localFileName)
        {
            Connect();

            using (Stream fileStream = File.OpenWrite(localFileName))
            {
                _sftp.DownloadFile(ftpFileName, fileStream);
            }
        }

        /// <summary>查询或读取 SSHHelper 所需的 Read All Bytes 数据，并按调用约定返回结果。</summary>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public byte[] ReadAllBytes(string ftpFileName)
        {
            Connect();

            return _sftp.ReadAllBytes(ftpFileName);
        }

        /// <summary>准备 SSHHelper 的 Open Read 交互状态并打开对应界面。</summary>
        /// <param name="path">path 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public Stream OpenRead(string path)
        {
            return _sftp.Open(path, FileMode.Open, FileAccess.Read);
        }

        /// <summary>整理 SSHHelper 的 Download File With Resume 数据，并生成可供调用方使用的输出。</summary>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        /// <param name="localFileName">需要读取、校验或转换的业务值。</param>
        public void DownloadFileWithResume(string ftpFileName, string localFileName)
        {
            DownloadFile(ftpFileName, localFileName);
        }

        /// <summary>负责 SSHHelper 的 Rename File 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="oldPath">oldPath 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="newPath">newPath 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        public void RenameFile(string oldPath, string newPath)
        {
            _sftp.RenameFile(oldPath, newPath);
        }

        /// <summary>查询或读取 SSHHelper 所需的 Get File List 数据，并按调用约定返回结果。</summary>
        /// <param name="folder">folder 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="filters">filters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public List<string> GetFileList(string folder, IEnumerable<string> filters)
        {
            Connect();

            var files = new List<string>();
            var sftpFiles = _sftp.ListDirectory(folder);
            foreach (var file in sftpFiles)
            {
                if (file.IsRegularFile && filters.Any(f => file.Name.EndsWith(f)))
                    files.Add(file.Name);
            }
            return files;
        }

        /// <summary>
        /// 上传指定目录文件
        /// </summary>
        /// <param name="localFileName">需要读取、校验或转换的业务值。</param>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        public void UploadFile(string localFileName, string ftpFileName)
        {
            Connect();

            var dir = Path.GetDirectoryName(ftpFileName);
            CreateDir(_sftp, dir);
            using (var fileStream = new FileStream(localFileName, FileMode.Open))
            {
                _sftp.UploadFile(fileStream, ftpFileName);
            }
        }

        /// <summary>接收并校验 SSHHelper 的 Upload File 输入，随后写入目标状态。</summary>
        /// <param name="bs">bs 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        public void UploadFile(byte[] bs, string ftpFileName)
        {
            Connect();

            var dir = Path.GetDirectoryName(ftpFileName);
            CreateDir(_sftp, dir);
            _sftp.WriteAllBytes(ftpFileName, bs);
        }

        /// <summary>接收并校验 SSHHelper 的 Upload File 输入，随后写入目标状态。</summary>
        /// <param name="fileStream">fileStream 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="ftpFileName">需要读取、校验或转换的业务值。</param>
        public void UploadFile(Stream fileStream, string ftpFileName)
        {
            Connect();

            var dir = Path.GetDirectoryName(ftpFileName);
            CreateDir(_sftp, dir);
            _sftp.UploadFile(fileStream, ftpFileName);
            fileStream.Dispose();
        }

        /// <summary>构建 SSHHelper 所需的 Create Dir 内容，并保证生成结果符合调用约定。</summary>
        /// <param name="sftp">sftp 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="dir">dir 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <exception cref="ArgumentNullException"></exception>
        private void CreateDir(SftpClient sftp, string dir)
        {
            ArgumentNullException.ThrowIfNull(dir);

            if (sftp.Exists(dir)) return;

            var index = dir.LastIndexOfAny(new char[] { '/', '\\' });
            if (index > 0)
            {
                var p = dir[..index];
                if (!sftp.Exists(p))
                    CreateDir(sftp, p);
                sftp.CreateDirectory(dir);
            }
        }

        /// <summary>释放 SSHHelper 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
        public void Dispose()
        {
            if (_sftp == null) return;

            if (_sftp.IsConnected)
                _sftp.Disconnect();
            _sftp.Dispose();
        }
    }
}