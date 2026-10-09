# RtspRelayService（RTSP 转推服务）

本服务为每个已配置的 RTSP 摄像头启动一个受监控的 FFmpeg 进程，并将每路视频流分别推送到对应的 ZLMediaKit RTMP 地址。

`Relay.InputTimeoutSeconds` 用于控制 RTSP 套接字的输入输出超时时间。当摄像头断电但 TCP 连接未正常关闭时，FFmpeg 会在达到该超时时间后退出，监督器随后开始执行重试流程。默认值为 10 秒。

本仓库还包含人员活动录像服务（ActivityRecordingService），见下方 [ActivityRecordingService（人员活动录像服务）](#activityrecordingservice人员活动录像服务) 章节。两个服务独立发布、独立安装。

## macOS 开发

环境要求：.NET 8 SDK 和 FFmpeg。

如果本地开发配置尚不存在，请创建一份不会被 Git 跟踪的开发配置：

```bash
cp -n src/RtspRelayService/appsettings.json \
  src/RtspRelayService/appsettings.Development.json
```

在 `appsettings.Development.json` 中填写本地视频流地址，然后运行：

```bash
DOTNET_ENVIRONMENT=Development \
Relay__FfmpegPath=/opt/homebrew/bin/ffmpeg \
dotnet run --project src/RtspRelayService --no-launch-profile
```

设置 `DOTNET_ENVIRONMENT=Development` 后，程序会在加载 `appsettings.json` 之后继续加载 `appsettings.Development.json`；环境变量会覆盖配置文件中的 FFmpeg 路径。真实的视频流地址只能写入已被忽略的开发配置文件，切勿提交账号、密码等凭据。

## 运行测试

```bash
dotnet test RtspRelayService.sln
```

## 发布 Windows x64 版本

以下操作在 macOS 的项目根目录中执行：

```bash
dotnet publish src/RtspRelayService/RtspRelayService.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o publish/rtsp-relay/windows-x64
```

发布结果位于 `publish/rtsp-relay/windows-x64`。由于使用了 `--self-contained true`，其中包含运行所需的 .NET 组件，目标 Windows 主机无须另外安装 .NET 运行时。

### 准备 Windows 版 FFmpeg

macOS 版 FFmpeg 不能在 Windows 中运行。请下载 Windows x64 版 FFmpeg，并将 `ffmpeg.exe` 放到以下位置：

```text
publish/rtsp-relay/windows-x64/ffmpeg/ffmpeg.exe
```

发布目录结构应类似：

```text
publish/rtsp-relay/windows-x64/
├── RtspRelayService.exe
├── appsettings.json
├── ffmpeg/
│   └── ffmpeg.exe
└── 其他发布文件
```

`appsettings.json` 中的 FFmpeg 路径应保持为：

```json
"FfmpegPath": "ffmpeg/ffmpeg.exe"
```

### 配置视频流

编辑发布目录中的 `appsettings.json`。多路摄像头可在 `Streams` 数组中配置，每路摄像头使用不同的 `Name` 和 `OutputUrl`：

```json
{
  "Relay": {
    "FfmpegPath": "ffmpeg/ffmpeg.exe",
    "InitialRestartDelaySeconds": 5,
    "MaxRestartDelaySeconds": 60,
    "StableRunSeconds": 60,
    "InputTimeoutSeconds": 10,
    "ShutdownTimeoutSeconds": 10,
    "LogRetentionDays": 14,
    "Streams": [
      {
        "Name": "camera-01",
        "Enabled": true,
        "InputUrl": "rtsp://username:password@192.0.2.10:554/stream",
        "OutputUrl": "rtmp://media.example.com/live/camera-01?sign=example-token"
      },
      {
        "Name": "camera-02",
        "Enabled": true,
        "InputUrl": "rtsp://username:password@192.0.2.11:554/stream",
        "OutputUrl": "rtmp://media.example.com/live/camera-02?sign=example-token"
      }
    ]
  }
}
```

真实地址中可能包含摄像头密码和 RTMP 推流签名，请勿将含有真实凭据的配置文件提交到 Git。

### 复制到 Windows

将 `publish/rtsp-relay/windows-x64` 中的全部文件复制到 Windows，例如：

```text
E:\RtspRelayService
```

不要只复制 `RtspRelayService.exe`，发布目录中的 DLL、配置文件和 `ffmpeg.exe` 都必须保留。还需将仓库中的 `deploy/windows` 目录复制进去，最终目录结构应类似：

```text
E:\RtspRelayService\
├── RtspRelayService.exe
├── appsettings.json
├── ffmpeg\
│   └── ffmpeg.exe
└── deploy\
    └── windows\
        ├── install-service.ps1
        └── uninstall-service.ps1
```

### 安装 Windows 服务

在 Windows 中以管理员身份运行 PowerShell：

```powershell
cd 'E:\RtspRelayService'
Set-ExecutionPolicy -Scope Process Bypass
.\deploy\windows\install-service.ps1 -InstallDirectory 'E:\RtspRelayService'
Get-Service RtspRelayService
```

`-InstallDirectory` 必须与程序实际所在目录一致。若不传该参数，脚本默认在 `C:\Program Files\RtspRelayService` 中查找程序。出现 `Executable not found` 时，可先检查文件是否存在：

```powershell
Test-Path 'E:\RtspRelayService\RtspRelayService.exe'
```

结果应为 `True`。

安装脚本会将 `RtspRelayService` 注册为自动启动的 Windows 服务，为服务异常退出配置三次恢复重启，并立即启动服务。注册服务之前，脚本会检查 `appsettings.json` 和 `ffmpeg\ffmpeg.exe` 是否存在，然后替换配置文件的访问控制列表（ACL），确保只有 `BUILTIN\Administrators` 和默认服务账户 `NT AUTHORITY\SYSTEM` 可以访问该文件。

查看服务状态和实际执行路径：

```powershell
Get-Service RtspRelayService
Get-CimInstance Win32_Service -Filter "Name='RtspRelayService'" |
  Select-Object Name, State, StartMode, PathName
```

常用服务命令：

```powershell
Start-Service RtspRelayService
Stop-Service RtspRelayService
Restart-Service RtspRelayService
```

可以使用以下命令检查配置文件的 ACL，而不会显示配置文件内容：

```powershell
$ConfigPath = 'E:\RtspRelayService\appsettings.json'
(Get-Acl -LiteralPath $ConfigPath).Access |
  Select-Object IdentityReference, FileSystemRights, AccessControlType, IsInherited
```

预期结果：只出现 `BUILTIN\Administrators` 和 `NT AUTHORITY\SYSTEM`；两条规则的 `AccessControlType` 都是 `Allow`，并且 `IsInherited` 都是 `False`。

### 更新程序

先停止服务，再用新的发布文件覆盖原目录。保留并核对实际使用的 `appsettings.json`，然后重新启动服务：

```powershell
Stop-Service RtspRelayService
# 将新的发布文件复制到 E:\RtspRelayService
Start-Service RtspRelayService
```

## 日志

日志保存在程序目录下的 `logs` 文件夹。使用上述 E 盘部署方式时，日志目录为：

```text
E:\RtspRelayService\logs\
```

服务总日志按天滚动，文件名类似 `relay-20260803.log`。此外，每个摄像头都有单独的日志文件：

```text
E:\RtspRelayService\logs\cameras\camera-01-20260803.log
E:\RtspRelayService\logs\cameras\camera-02-20260803.log
```

摄像头日志包含对应一路的 FFmpeg 输出、退出、重试和恢复信息；这些信息也会同时写入服务总日志。摄像头名称中的路径符号等特殊字符会被安全编码后用于文件名。

总日志和摄像头日志均为单个文件最大 100 MB，超过限制后生成 `_001`、`_002` 等分片。日志默认保留 14 天，超过保留时间的旧文件由程序自动清理。卸载 Windows 服务不会删除已有配置和日志。

保留天数由 `appsettings.json` 中的以下配置控制：

```json
"LogRetentionDays": 14
```

修改后需要重启服务。实时查看当天日志：

```powershell
Get-Content "E:\RtspRelayService\logs\relay-$(Get-Date -Format yyyyMMdd).log" -Wait
```

查看全部日志及其大小和更新时间：

```powershell
Get-ChildItem 'E:\RtspRelayService\logs' |
  Sort-Object LastWriteTime -Descending |
  Select-Object Name, Length, LastWriteTime
```

## 验收检查

1. 确认每一路已启用的视频流都能通过 ZLMediaKit 正常播放。
2. 断开其中一个摄像头，确认其他视频流仍然正常运行。
3. 重新连接摄像头，确认失败的视频流在退避等待后恢复推流。
4. 停止服务，确认没有残留的 `ffmpeg.exe` 进程。
5. 重启 Windows，确认服务和所有视频流都能自动恢复。
6. 检查日志，确认密码和 RTMP 地址的查询参数已被脱敏。

## 卸载服务

在安装目录中，以管理员身份运行以下 PowerShell 命令：

```powershell
.\deploy\windows\uninstall-service.ps1
```

卸载脚本只会删除 `RtspRelayService` 的 Windows 服务注册信息，安装目录中的配置文件和日志会保留。

# ActivityRecordingService（人员活动录像服务）

第一版只支持检测到人员活动后录制事件片段。录像和事件片段直接复制输入视频轨，因此输入必须是 H.264。第一版不支持 H.265，也不支持四路摄像头的全天连续录制。

默认 `ActivityRecording.Enabled` 为 `false`。这种配置下 Windows 服务可以正常安装并保持空闲；只有把服务级 `Enabled` 和每个需要的摄像头项的 `Enabled` 都设为 `true`，对应摄像头管线才会启动。不要为了启用服务而省略单个摄像头的 `Enabled`。

每路摄像头的 `Name` 是技术标识（ASCII，用作目录名和数据库键），可选的 `DisplayName` 是面向展示的名称（例如中文，最长 64 字符）。事件接口会通过 `cameraDisplayName` 字段返回显示名称；未配置或摄像头已从配置中移除时回退为 `Name`。

事件接口的时间字段（`startedAt`、`endedAt`、`expiresAt`）统一输出为北京时间 `yyyy-MM-dd HH:mm:ss` 格式字符串（例如 `2026-10-09 13:15:40`），秒级精度、无时区后缀，可直接用于界面显示。筛选参数 `fromUtc`、`toUtc` 不受影响，继续接受任意偏移量的 ISO 8601 时间。

除事件维度外，`GET /recordings/api/files` 提供按文件展开的视图：每行对应一个录像文件，带所属事件与摄像头信息，状态为 `Ready` 的文件直接内嵌签名播放链接（`playbackUrl`），前端一次调用即可渲染文件列表；支持 `camera`、`offset`、`limit` 参数。

## 本地开发和测试

环境要求：.NET 8 SDK、FFmpeg 和一份与人员检测实现兼容的 ONNX 模型文件。

本地开发配置不会被 Git 跟踪。首次开发时从示例配置复制一份并填写真实值（摄像头地址、`RecordingRoot`、`FfmpegPath`、`Detection:ModelPath`、`ApiKeys`、签名密钥等），开发时把服务级和摄像头级 `Enabled` 都设为 `true`：

```bash
cp -n src/ActivityRecordingService/appsettings.example.json \
  src/ActivityRecordingService/appsettings.Development.json
```

`FfmpegPath` 填本机 FFmpeg 绝对路径（Homebrew 安装为 `/opt/homebrew/bin/ffmpeg`）。`Detection:ModelPath` 的相对路径基于运行目录解析，本地可指向仓库外的模型文件副本。真实摄像头地址、凭据和密钥只能写入这份被忽略的开发配置。

运行服务（`launchSettings.json` 会自动启用 Development 环境并加载上面的开发配置，API 默认监听 `127.0.0.1:5090`）：

```bash
dotnet run --project src/ActivityRecordingService
```

只运行本服务的测试：

```bash
dotnet test tests/ActivityRecordingService.Tests
```

如需绕过 launch profile 手动控制环境，使用 `DOTNET_ENVIRONMENT=Development dotnet run --project src/ActivityRecordingService --no-launch-profile`。

## Windows 部署

### 发布和目录布局

发布需要 .NET 8 SDK。以下命令在项目根目录执行：

```bash
dotnet publish src/ActivityRecordingService/ActivityRecordingService.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o publish/activity-recording/windows-x64
```

`--self-contained true` 会包含 .NET 8 运行时，目标 Windows 主机不需要另行安装 .NET Runtime。发布目录必须在 Windows 主机上准备好以下布局；不要只复制可执行文件：

```text
E:\ActivityRecordingService\
├── ActivityRecordingService.exe
├── appsettings.json
├── ffmpeg\
│   └── ffmpeg.exe
├── logs\
├── models\
│   └── yolo11n.onnx
└── deploy\
    └── windows\
        ├── install-activity-recording-service.ps1
        └── uninstall-activity-recording-service.ps1
```

`E:\ActivityRecordingService` 只是路径示例；应替换为本机实际的专用安装目录。macOS 或 Linux 构建的 FFmpeg 不能用于 Windows。下载受信任的 Windows x64 FFmpeg 后，把其中的 `ffmpeg.exe` 放到 `ffmpeg\ffmpeg.exe`，并在 Windows 中用 `.\ffmpeg\ffmpeg.exe -version` 验证。

ONNX 模型必须放在 `models\yolo11n.onnx`。只使用来自受信任来源并且与人员检测实现兼容的模型；不要把模型、真实 `appsettings.json`、录像、日志或数据库提交到 Git。如果发布目录包含构建机上的本地 `appsettings.json`，更新部署时必须保留目标主机上已经配置好的那份文件，安装脚本不会覆盖它。

### 配置和密钥

首次部署时，把仓库中的 `src/ActivityRecordingService/appsettings.example.json` 复制到安装目录并重命名为 `appsettings.json`，然后只在本机编辑。示例文件中的占位值不能用于生产。真实 API 地址、绑定地址、JWT 签名密钥、播放 URL 签名密钥、API 密钥派生值和摄像头凭据只能保存在这份被忽略的本机文件中。

API 客户端密钥应在安全设备或密钥管理工具中生成并保存。使用随机 256 位密钥和随机 256 位 salt；API 服务保存 Base64 编码的 salt 和 PBKDF2-SHA256 hash，不保存原始密钥。下面命令在 PowerShell 7/.NET 8 中生成一组新值；不要在文档、提交、聊天或共享终端记录中写入已有密钥：

```powershell
$apiKey = [Convert]::ToHexString(
  [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$iterations = 210000
$hash = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
  [System.Text.Encoding]::UTF8.GetBytes($apiKey),
  $salt,
  $iterations,
  [System.Security.Cryptography.HashAlgorithmName]::SHA256,
  32)
[pscustomobject]@{
  Salt = [Convert]::ToBase64String($salt)
  Hash = [Convert]::ToBase64String($hash)
  Iterations = $iterations
} | ConvertTo-Json
```

macOS/Linux 上用 Python 生成等价的一组值（PBKDF2-SHA256，参数与服务端一致）：

```bash
python3 -c "
import secrets, hashlib, base64
key = secrets.token_urlsafe(32)
salt = secrets.token_bytes(32)
h = hashlib.pbkdf2_hmac('sha256', key.encode(), salt, 210000, 32)
print('Key: ', key)
print('Salt:', base64.b64encode(salt).decode())
print('Hash:', base64.b64encode(h).decode())
"
```

只在受信任的管理工作站上运行生成命令，立即把 `$apiKey` 存入审批过的密钥管理工具。不要显示、记录、截屏、粘贴、导出或通过聊天、邮件、工单、共享终端历史传输原始 API key。确需转移时，只使用审批过的密钥管理通道；`appsettings.json` 中只写入 `Salt`、`Hash` 和 `Iterations`。轮换密钥时创建新的随机 salt 和 hash，更新客户端后再移除旧条目。

JWT 签名密钥和播放 URL 签名密钥都必须是至少 32 个 UTF-8 字节的独立随机值。两者不能相同，也不要复用 API 客户端密钥。

生成这两把签名密钥（PowerShell 在 Windows 上执行，bash 在 macOS/Linux 上执行）。输出值直接填入 `appsettings.json`，两把各自独立生成：

```powershell
function New-SigningKey {
    $bytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes)
}

"JwtSigningKey:      $(New-SigningKey)"
"PlaybackUrlSigningKey: $(New-SigningKey)"
```

```bash
echo "JwtSigningKey:         $(head -c 48 /dev/urandom | base64)"
echo "PlaybackUrlSigningKey: $(head -c 48 /dev/urandom | base64)"
```

换用新的 `JwtSigningKey` 后，所有已签发的 JWT 立即失效，客户端需要重新调用 token 接口换取新令牌；换 `PlaybackUrlSigningKey` 会使已发出的播放直链签名立即失效，重新获取播放 URL 即可。两个密钥都只在服务器本机的 `appsettings.json` 中保存。

### WireGuard 和 API 绑定

先规划主机在 WireGuard 网络内的唯一地址，例如使用 `<wireguard-host-address>` 这一本地部署占位符。`ActivityRecording:Api:Host` 必须填写该主机的 WireGuard 地址本身；Kestrel 只监听该地址和配置的 `Api:Port`，不会监听 `0.0.0.0`。生产主机不要在公共网卡上发布播放 API，也不要添加公网端口映射。

Windows 侧安装官方 WireGuard 客户端，使用 `deploy/windows/wireguard-client.conf.example` 作为隧道配置模板；注意 `SaveConfig` 是 Linux wg-quick 的选项，Windows 客户端配置中不要包含该行。密钥对在任意一端用 `wg genkey` 与 `wg pubkey` 生成后分别填入两端配置。开机自动连接使用 WireGuard 界面的"激活隧道"加自启动选项，或以管理员运行 `wireguard.exe /installtunnelservice <配置文件路径>`。

Windows 防火墙规则应只允许 WireGuard 接口来源访问 API 端口。如果使用子网策略，把来源限制到确实需要播放的 WireGuard 客户端子网。浏览器或客户端应通过 HTTPS 终端访问该私有网络终端，不要绕过终端直接暴露 HTTP API。

隧道配置完成后的验证顺序：

```powershell
# Windows：确认隧道内地址已生效，且能访问 CentOS 对端
ping 10.8.0.1
```

```bash
# CentOS：通过隧道访问 Windows 上的录像 API
curl http://10.8.0.2:5090/recordings/api/health
```

两步都通过后再验证公网 HTTPS 入口 `https://<existing-domain>/recordings/api/health`。若 Windows 的 ping 不通，先检查两端的 `PublicKey` 是否互换填写、`Endpoint` 是否为 CentOS 公网地址；若 CentOS 访问不到 `10.8.0.2:5090`，确认 `ActivityRecording:Api:Host` 已配置为 `10.8.0.2` 且 Windows 防火墙放行来自 WireGuard 接口的 `5090` 端口。

### CentOS 公共 HTTPS 边界

CentOS 只作为现有域名和 HTTPS 证书所在公共终端，把 `/recordings/` 反向代理到 Windows WireGuard 地址 `10.8.0.2:5090`。参考配置是 `deploy/centos/wireguard-server.conf.example` 和 `deploy/centos/nginx-activity-recording.conf.example`。先把域名、证书路径和密钥占位符替换为本机真实值，再在 CentOS 上执行 `nginx -t` 和 `wg-quick strip /etc/wireguard/wg0.conf` 检查；不要提交真实密钥、证书或域名配置。

生成 WireGuard 密钥时在 CentOS 上保持密钥文件私有：

```bash
sudo sh -c 'umask 077; wg genkey > /etc/wireguard/centos-private.key'
sudo sh -c 'wg pubkey < /etc/wireguard/centos-private.key > /etc/wireguard/centos-public.key'
sudo chmod 600 /etc/wireguard/wg0.conf
sudoedit /etc/wireguard/wg0.conf
```

`/etc/wireguard/wg0.conf` 的所有者必须是 `root:root`，权限必须是 `0600`。配置中的私钥只能使用本机生成的真实值；Windows 配置只需要 CentOS 的公钥。服务器把 Windows peer 的 `AllowedIPs` 固定为 `10.8.0.2/32`；Windows 端不要为了播放 API 路由全部流量。确认 Windows 侧主动发起 WireGuard 连接并配置 `PersistentKeepalive = 25` 后，启用：

```bash
sudo wg-quick up wg0
sudo systemctl enable wg-quick@wg0
```

Windows 端示例值如下。`PrivateKey` 必须使用本机生成的真实私钥，`PublicKey` 使用 CentOS 公钥，端点只保留部署占位符：

```ini
[Interface]
PrivateKey = <windows-wireguard-private-key>
Address = 10.8.0.2/24

[Peer]
PublicKey = <centos-wireguard-public-key>
AllowedIPs = 10.8.0.1/32
Endpoint = <centos-public-endpoint>:51820
PersistentKeepalive = 25
```

`AllowedIPs = 10.8.0.1/32` 只把 CentOS 的 WireGuard 地址送入隧道；不要使用 `0.0.0.0/0`、`::/0` 或其他全隧道路由，也不要为播放 API 添加静态默认路由。

Nginx 示例是完整独立配置，便于单独检查；`/recordings`（无尾斜杠）和 `/recordings/` 以外的路径都会返回 `404`，默认 Nginx 文档根目录不会被暴露。如果 CentOS 已有同一域名的 HTTPS `server` 块，不要复制或新增重复 `server_name` 的示例 `server` 块。只把 `limit_conn_zone`、活动录像访问日志 `log_format`（如选择这套日志格式且尚未存在）和完整的 `/recordings/` `location` 块合并进现有配置；TLS、域名和现有 HTTPS 行为留在原有 `server` 块中。示例固定代理路径和上游，只允许 `GET`、隐含的 `HEAD` 和换发 JWT 的 `POST`；转发 `Range` 和 `If-Range`，保留 `Accept-Ranges` 和 `Content-Range` 响应头，限制连接数和请求体大小，并设置上游超时。`proxy_buffering off`、`proxy_cache off` 和 `proxy_max_temp_file_size 0` 防止 CentOS 缓存或落盘代理录像。不要给它添加 `alias`、`root`、`proxy_cache_path` 或其他可访问本地文件的位置。

检查防火墙时，先审计当前生效和永久配置，再删除与目标无关的条目。先确认可以从带外或本地控制台维护主机；是否保留、收紧或移除 SSH 由运维方按现有安全基线决定，本指南不新增也不盲删 SSH 规则。如果保留受控 SSH 管理通道，应使用运维方已批准的来源限制或端口策略，并将其视为管理例外而不是活动录像公共暴露面。

```bash
sudo firewall-cmd --get-active-zones
sudo firewall-cmd --zone=<active-zone> --list-all
sudo firewall-cmd --permanent --zone=<active-zone> --list-all
for zone in $(sudo firewall-cmd --permanent --get-zones); do
  echo "== permanent: $zone =="
  sudo firewall-cmd --permanent --zone="$zone" --list-all
done
sudo firewall-cmd --direct --get-all-rules
sudo firewall-cmd --permanent --direct --get-all-rules
```

对选定活动 zone 的永久配置逐项移除非本边界需要的服务、端口、rich rule 和 direct rule；direct rule 必须按输出中的完整规则逐条精确移除。例如（不要原样套用名称）：

```bash
zone=<selected-active-zone>
sudo firewall-cmd --permanent --zone="$zone" --remove-service=<unrelated-service>
sudo firewall-cmd --permanent --zone="$zone" --remove-port=<unrelated-port-and-protocol>
sudo firewall-cmd --permanent --zone="$zone" --remove-rich-rule='<full-unrelated-rich-rule>'
sudo firewall-cmd --permanent --direct --remove-rule <ipv> <table> <chain> <priority> <full-matching-rule-arguments>
```

移除完成后，目标边界只新增以下两项。若运维方明确要求保留受控 SSH 管理通道，单独保留该已批准策略；除该管理决策外，活动录像公共暴露只应有 HTTPS 和 WireGuard UDP：

```bash
zone=<selected-active-zone>
sudo firewall-cmd --permanent --zone="$zone" --add-service=https
sudo firewall-cmd --permanent --zone="$zone" --add-port=51820/udp
sudo firewall-cmd --reload
sudo firewall-cmd --zone="$zone" --list-all
sudo firewall-cmd --permanent --zone="$zone" --list-all
sudo firewall-cmd --direct --get-all-rules
sudo firewall-cmd --permanent --direct --get-all-rules
```

最终核对活动 zone 和永久配置：活动录像相关允许项只有 `https` 与 `51820/udp`；rich rule 和 direct rule 为空，除非运维方记录了明确的受控 SSH 或其他已批准管理例外。不要放行 `5090/tcp`，不要添加 NAT 转发或 MASQUERADE。生产检查通过后执行 `sudo nginx -t && sudo systemctl reload nginx`。

### 磁盘和 ACL

录像根目录应是安装目录之外的专用子目录，例如示例配置中的占位路径 `E:\ActivityRecordings`。不要把卷根目录或 UNC 共享根目录直接作为 `RecordingRoot`；`RecordingRoot` 不能等于安装目录、位于安装目录内、包含安装目录，或与安装目录路径重叠。安装脚本会在创建目录或修改 ACL 前检查这些路径约束。该目录所在卷需要预留 540 GiB 配额和 60 GiB 空闲水位以外的空间。SQLite 数据库和录像会写入该目录，日志写入安装目录下的 `logs`。

安装脚本支持两种账户：

```powershell
# 使用 LocalSystem
.\deploy\windows\install-activity-recording-service.ps1 `
  -InstallDirectory 'E:\ActivityRecordingService'

# 使用最低权限专用服务账户
$serviceAccount = Get-Credential
.\deploy\windows\install-activity-recording-service.ps1 `
  -InstallDirectory 'E:\ActivityRecordingService' `
  -ServiceAccount $serviceAccount
```

脚本要求管理员权限，始终检查可执行文件、真实 `appsettings.json`、`RecordingRoot` 的专用路径和路径隔离关系，创建 `logs` 与受 ACL 保护的 `RecordingRoot`，并且不允许录像根目录位于安装树内。它不会创建或覆盖 `appsettings.json`。`ActivityRecording:Enabled=false` 时支持“禁用优先”安装：此时只需要可执行文件和配置，可先安装并保持空闲。启用模式还要求配置指向的 `ffmpeg.exe` 和 ONNX 模型存在；后续启用时补齐二者、把服务级和摄像头级 `Enabled` 都设为 `true`，再重启服务。脚本会把安装目录的保护 ACL 与父目录继承断开：管理员保留完全控制；服务账户对可执行文件、DLL、FFmpeg、模型和 `appsettings.json` 只有读和执行，并有显式写、删除、改权限和取得所有权的拒绝规则，因此继承或组授权无法重新授予写入或删除权限。`logs` 和专用 `RecordingRoot` 是仅有的服务账户写入点，均授予修改权限。

如果选择专用账户，先在本地安全策略中授予该账户“作为服务登录”权限，再运行安装脚本。账户应没有交互登录、远程桌面和管理员权限。可以使用以下命令核对 ACL，而不显示配置内容：

```powershell
(Get-Acl -LiteralPath 'E:\ActivityRecordingService\appsettings.json').Access |
  Select-Object IdentityReference, FileSystemRights, AccessControlType, IsInherited
```

### 保留策略和容量

默认事件文件保留 7 天；总量配额默认 540 GiB；记录卷剩余空间低于 60 GiB 时停止开始新事件。启动清理器和每小时清理循环会删除超过保留期或为释放配额而选择的事件。改变这些值前确认卷容量、留存要求和配额上限一致，修改 `appsettings.json` 后重启服务。

### 安装、更新和卸载

安装后服务名为 `ActivityRecordingService`，启动类型为自动，失败恢复动作是 5 秒、10 秒和 60 秒三次重启，每 24 小时重置失败计数。查看状态和实际二进制路径：

```powershell
Get-Service ActivityRecordingService
Get-CimInstance Win32_Service -Filter "Name='ActivityRecordingService'" |
  Select-Object Name, State, StartMode, PathName
```

启动、停止和重启（需要管理员 PowerShell）：

```powershell
Start-Service ActivityRecordingService
Stop-Service ActivityRecordingService
Restart-Service ActivityRecordingService
```

停止服务会等待事件收尾并结束 FFmpeg 进程树后再退出；修改 `appsettings.json` 后使用 `Restart-Service` 让新配置生效。等价的 `sc.exe` 命令是 `sc.exe start|stop ActivityRecordingService`。

更新程序时先停止服务，复制新的发布文件，保留目标主机的 `appsettings.json`、`models`、`logs` 和录像根目录，然后启动服务。卸载使用：

```powershell
.\deploy\windows\uninstall-activity-recording-service.ps1
```

卸载脚本只停止并删除 Windows 服务注册。它不会删除 `appsettings.json`、录像、日志、SQLite 数据库或 ONNX 模型。

## 验收清单

在实际 Windows 11 主机和 CentOS 公网边界上完成以下检查；检查时使用真实摄像头源、专用录像卷和已替换占位符的生产配置，但不要泄露配置或密钥。

1. 默认安全状态：服务安装后自动启动但保持空闲，`ActivityRecording:Enabled` 与所有摄像头项的 `Enabled` 都是 `false`；日志确认没有启动录像、检测或 FFmpeg 管线。确认配置后按受控流程启用所需摄像头。
2. 四路稳定性：四台启用摄像头持续运行代表性时长，记录 CPU、专用内存、句柄、FFmpeg 进程数和事件/文件数。期间摄像头故障与恢复，最终没有失控进程、内存持续增长、数据库锁等待或未完成事件泄漏。
3. 30 秒预录：对每路摄像头触发一次人员活动，检查事件开始时间、预录配置和首个 MP4 内容包含触发前约 30 秒画面；不同摄像头互不串数据。
4. 长事件分片：制造超过单个事件分片时长的事件，确认 MP4 parts 的编号、时长、字节数和状态连续可播，超过 2 小时上限或单 part 上限时正确结束或分片，没有丢失或不完整 part 被返回播放。
5. 磁盘水位：核对配额、保留期和 60 GiB 最低空闲水位。用受控测试卷或配额让可用空间到达水位，确认新事件被阻止、`/recordings/api/health` 显示阻断原因、每小时清理和保留期删除释放空间后恢复，且删除只发生在 `RecordingRoot` 内。
6. 断线恢复：断开 RTSP、WireGuard 可用性和 Windows 网络后恢复连接，确认检测管线退避重连、事件不重叠、SQLite 事务一致，API 最终能列出事件。
7. 签名 URL 过期：持 JWT 获取播放 URL 后先播放成功，等到 `expiresAt` 后重放同一 URL 应得到 401；修改签名、过期时间或文件 ID 也应得到 401/404。
8. 隧道限制：`netstat`/PowerShell 确认 Kestrel 只绑定 `10.8.0.2:5090`。从 CentOS 通过 `http://10.8.0.2:5090/recordings/api/health` 可访问；从公网和宽带路由外网地址直接访问 `5090` 不可达，Windows 路由器没有入站映射。
9. HTTPS 播放：经现有域名 HTTPS 访问事件列表和播放 URL，MP4 能顺序播放；拖动到开头、中间、结尾和已缓存区间之外时，请求携带 `Range`，响应包含正确 `206`、`Content-Range` 和 `Accept-Ranges: bytes`。其他 HTTP 方法返回拒绝。
10. CentOS 无录像：播放前后检查 `/var/lib/nginx`、`/var/cache/nginx`、示例中没有配置的任何代理存储目录和常见可写目录，确认没有事件 SQLite 数据库、活动录像 MP4 或录像备份。Nginx 访问日志不应包含签名 query 参数。
11. Windows/UNC 和服务 ACL：在真实 Windows 主机分别用本地专用卷和 UNC 路径配置 `RecordingRoot`。启动后确认 SQLite、MP4 写入该路径，容量探测读取 UNC 卷空闲空间并执行水位策略；安装脚本和专用服务账户 ACL 检查通过，管理员保留控制权，服务账户除 `logs` 和 `RecordingRoot` 外没有写/删除权限，失败恢复动作生效。
12. 重启恢复：重启 Windows 后不手工启动服务或 WireGuard，确认服务自动恢复、Windows 发起 WireGuard 握手、CentOS 能重新访问 `10.8.0.2:5090`，随后 API 和 HTTPS 播放恢复正常。
13. 路径别名检查：安装目录与 `RecordingRoot` 使用真实的规范化长路径。用 `Get-Item -Force` 检查 `LinkType`/`Target`，用 `cmd /c dir /x` 检查 8.3 短名；确认没有 reparse point、junction、symlink、subst 或短名别名使两个路径在安装/运行时指向彼此或重叠。避免把短路径、映射别名或 reparse point 写入生产配置。

全部通过后，保存脱敏的验收证据（时间、命令摘要、指标和结果），不要在证据中包含 API key、JWT、播放签名、摄像头凭据或未公开的主机地址。
