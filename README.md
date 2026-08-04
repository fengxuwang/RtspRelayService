# RTSP 转推服务

本服务为每个已配置的 RTSP 摄像头启动一个受监控的 FFmpeg 进程，并将每路视频流分别推送到对应的 ZLMediaKit RTMP 地址。

`Relay.InputTimeoutSeconds` 用于控制 RTSP 套接字的输入输出超时时间。当摄像头断电但 TCP 连接未正常关闭时，FFmpeg 会在达到该超时时间后退出，监督器随后开始执行重试流程。默认值为 10 秒。

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
  -o publish/windows-x64
```

发布结果位于 `publish/windows-x64`。由于使用了 `--self-contained true`，其中包含运行所需的 .NET 组件，目标 Windows 主机无须另外安装 .NET 运行时。

### 准备 Windows 版 FFmpeg

macOS 版 FFmpeg 不能在 Windows 中运行。请下载 Windows x64 版 FFmpeg，并将 `ffmpeg.exe` 放到以下位置：

```text
publish/windows-x64/ffmpeg/ffmpeg.exe
```

发布目录结构应类似：

```text
publish/windows-x64/
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

将 `publish/windows-x64` 中的全部文件复制到 Windows，例如：

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
