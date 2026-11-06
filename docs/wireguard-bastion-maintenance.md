# WireGuard 前置机内网维护与部署通用流程

## 1. 适用场景

本流程适用于多个医院、多个生产网段和多个前置机的维护部署。开发人员先通过共享 WireGuard 网络建立到指定前置机的独立 SSH 会话，再由该前置机访问所属医院的内网服务器。

核心原则是：**中心 WireGuard 网络只负责到前置机；医院内网访问在前置机内部完成。** 不把所有医院内网发布到中心网络，也不要求所有前置机开启三层转发。

## 2. 通用拓扑

```text
开发机 WireGuard 地址 WG_CLIENT_IP
        |
        | WireGuard，仅访问 WireGuard 管理网段 WG_NETWORK
        v
中心节点（可选，仅负责隧道汇聚）
        |
        v
医院前置机 WG_GATEWAY_IP / LAN_GATEWAY_IP
        |
        | 前置机所属医院内网 LAN_PREFIX
        v
目标服务器 TARGET_IP:TARGET_PORT
```

每个医院建立一条独立映射：

| 参数 | 含义 | 示例 |
|---|---|---|
| `SITE_NAME` | 医院或站点名称 | 医院 A |
| `WG_GATEWAY_IP` | 前置机 WireGuard 地址 | `10.66.66.8` |
| `LAN_GATEWAY_IP` | 前置机内网地址 | `192.168.4.238` |
| `LAN_PREFIX` | 该医院生产网段 | `192.168.2.0/24` |
| `TARGET_IP` | 具体目标服务器 | `192.168.2.200` |
| `TARGET_PORT` | 维护端口 | `5985` 或 `22` |

示例地址仅用于说明格式，新增医院必须使用该医院自己的参数。

## 3. 连接边界

- 开发机只连接目标前置机的 `WG_GATEWAY_IP`。
- 目标服务器地址只在前置机内部使用。
- 不为“方便访问”把所有 `LAN_PREFIX` 加入中心节点的统一 Peer 路由。
- 不修改其他医院、其他前置机或其他开发人员的配置。
- 前置机是否开启 IP 转发，取决于该医院的网络设计；本堡垒机模式默认保持关闭。

如果确实需要开发机直接访问某医院网段，应单独设计、审批和验证该医院的路由、返回路径、防火墙和审计策略，不能套用本流程的堡垒机模式。

## 4. 多人独立 SSH 连接

每个开发人员使用自己的 SSH 密钥和独立会话。新增密钥只能追加到前置机授权文件，不能覆盖文件、删除他人公钥、修改 `sshd_config` 或重启 `sshd`。

Windows OpenSSH 管理员授权文件通常为：

```text
C:\ProgramData\ssh\administrators_authorized_keys
```

追加前先备份并查看：

```powershell
$auth = 'C:\ProgramData\ssh\administrators_authorized_keys'
Copy-Item $auth "$auth.bak-$(Get-Date -Format yyyyMMddHHmmss)" -ErrorAction SilentlyContinue
Get-Content -Path $auth
```

只追加新公钥并避免重复：

```powershell
$key = 'ssh-ed25519 AAAA... unique-comment'
if (-not (Select-String -Path $auth -Pattern 'unique-comment' -SimpleMatch -Quiet -ErrorAction SilentlyContinue)) {
    Add-Content -Path $auth -Value $key
}
```

必须确认一个公钥占一行。不要使用覆盖写入或删除整个授权文件。

## 5. 开发机连接前置机

```powershell
$key = 'C:\Users\<开发者>\.ssh\<site>_front_ed25519'
ssh -i $key `
  -o KexAlgorithms=curve25519-sha256,diffie-hellman-group14-sha256 `
  -o BatchMode=yes `
  Administrator@<WG_GATEWAY_IP> hostname
```

每次维护使用新的 SSH 进程或会话。先确认返回的主机名、账号和站点，再执行目标服务器操作。

## 6. 前置机内访问 Windows 服务器

先替换站点参数进行端口和协议验证：

```powershell
Test-WSMan <TARGET_IP> -Port 5985
```

再使用目标服务器自己的凭据建立会话：

```powershell
$cred = Get-Credential
$s = New-PSSession -ComputerName <TARGET_IP> -Port 5985 -Credential $cred -Authentication Negotiate
Invoke-Command -Session $s -ScriptBlock { hostname; ipconfig }
Remove-PSSession $s
```

密码只在交互式凭据窗口输入，不写入脚本、参数、日志或文档。

## 7. 前置机内访问 Linux 服务器

```powershell
Test-NetConnection <TARGET_IP> -Port 22
ssh <用户名>@<TARGET_IP>
```

如果目标使用非标准 SSH 端口，显式指定：

```powershell
ssh -p <TARGET_PORT> <用户名>@<TARGET_IP>
```

## 8. 部署流程

1. 根据站点清单选择 `SITE_NAME`、前置机和目标服务器。
2. 通过 WireGuard 新建独立 SSH 会话到该站点前置机。
3. 在前置机确认主机名、内网地址和目标端口。
4. 备份目标文件或配置。
5. 上传或复制部署包到目标服务器。
6. 在目标服务器执行发布、服务重启或数据库脚本。
7. 立即验证端口、服务、日志和关键业务接口。
8. 记录站点、目标、版本、时间、操作者、验证结果和回滚命令。

## 9. 站点接入清单

新增医院时记录以下信息：

```text
SITE_NAME=
WG_GATEWAY_IP=
LAN_GATEWAY_IP=
LAN_PREFIX=
TARGET_IPS=
WINDOWS_PORTS=
LINUX_PORTS=
SSH_USER=
WINRM_AUTHENTICATION=
ROLLBACK_CONTACT=
```

先验证“开发机到前置机”，再验证“前置机到目标”。任何一段失败都只处理对应站点，不修改其他医院的隧道配置。

## 10. 故障排查

### 开发机到前置机失败

```powershell
Test-NetConnection <WG_GATEWAY_IP> -Port 22
ssh -vvv -i <私钥> Administrator@<WG_GATEWAY_IP>
```

区分隧道不通、主机指纹失败、KEX 算法不兼容和 SSH 认证失败。不要使用 `StrictHostKeyChecking=no` 绕过主机校验。

### 前置机到 Windows 目标失败

```powershell
Test-NetConnection <TARGET_IP> -Port 5985
Test-WSMan <TARGET_IP> -Port 5985
```

检查目标服务器 WinRM 服务、监听器、防火墙、账号权限和站点内网路由。

### 前置机到 Linux 目标失败

```powershell
Test-NetConnection <TARGET_IP> -Port 22
```

检查 Linux 的 `sshd`、防火墙、监听地址、VLAN 和站点交换网络。

## 11. 已验证站点示例

本次已验证站点参数：

```text
SITE_NAME=当前前置机
WG_GATEWAY_IP=10.66.66.8
LAN_GATEWAY_IP=192.168.4.238
LAN_PREFIX=192.168.2.0/24
WINDOWS_TARGET=192.168.2.200:5985
LINUX_TARGET=192.168.2.250:22
```

验证结果：Windows 目标 WinRM 成功；Linux 目标 `22` 端口本次未连通，需要单独检查 Linux SSH 服务或防火墙。

## 12. 禁止事项

- 不覆盖共享授权文件。
- 不删除或修改他人的公钥。
- 不停止共享 `sshd` 或远程协助软件。
- 不在中心节点无边界发布医院生产网段。
- 不在未确认返回路由、防火墙和审计范围时开启跨站点转发。
- 不在脚本、参数、日志和文档中保存密码或私钥。
