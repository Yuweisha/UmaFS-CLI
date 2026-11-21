# UmaFS-CLI

赛马娘（ウマ娘 プリティーダービー）资源浏览器：CLI 浏览 / 解包，以及 WinFsp 只读虚拟盘挂载。

数据来自本机已安装的游戏（Steam / DMM / Cygames 客户端），不修改游戏任何文件。

## 特性

- **CLI 浏览**：按清单路径列目录、查看条目详情、搜索、统计
- **虚拟盘挂载**：把 376,048 条资源挂成只读盘符，文件内容是**解密后**的原文
- **AB 包解包**：列包内对象，纹理直接导出 PNG，其它对象导出原始序列化数据
- **自动补扩展名**：`.unity3d` / `.awb` / `.usm` / `.otf` / `.lz4` / `.manifest`
- **WinFsp 自动安装**：未安装时用内置 MSI 静默安装（需要管理员）
- **配置持久化**：首次探测到游戏目录后自动记住，之后无需再指定
- **缺失数据过滤**：清单里有但本地没有数据的条目默认不列出，避免 AssetStudio 等工具报错

## 快速开始

```
umafs stats                              查看清单概况
umafs ls chara/chr1001                   列目录
umafs info chara/chr1001/chr_icon_1001   条目详情
umafs cat chara/chr1001/chr_icon_1001    输出解密内容到 stdout
umafs export chara/chr0000 D:\out        导出整个目录
umafs find chr_icon                      按路径搜索
umafs locate chr_icon_1001               查对象属于哪个 AB 包
umafs bundle chara/chr1001/chr_icon_1001 列包内对象
umafs extract chara/chr1001/chr_icon_1001 D:\tex   解包
umafs mount -m U:                        挂载到 U:
umafs check                              核对清单与本地数据，列出缺失条目
```

## 后缀说明

清单里的原始名称**大多没有扩展名**（只有 Sound 的 `.acb/.awb`、Movie 的 `.usm`、
Font 的 `.otf`、Master 的 `.lz4` 自带）。

`.unity3d` 是本工具按清单条目类型补的展示名：`Default` 类共 341,397 条，
400 条采样全部命中 `UnityFS` 魔数。原名与带后缀的名字都能解析。

不需要后缀用 `--no-ext`，或 `umafs config ext off` 永久关闭。

## 游戏目录优先级

`-r <路径>` / 环境变量 `UMAFSPATH` > `config.json` > 自动探测。

自动探测覆盖 `LocalLow\Cygames`（Cygames 客户端）、各盘 `DMMGamePlayer`、
Steam `libraryfolders.vdf` 里所有库。首次探测成功会自动写入
`%LOCALAPPDATA%\UmaFs\config.json`。

配置：`umafs config show | set-root <路径> | auto | unset | ext on|off | path`

## 挂载说明

WinFsp 是**内核驱动**，无法免安装；程序内嵌官方 MSI（2.1.25156），`mount` 时检测缺失则静默安装：

- 已有驱动且版本匹配 → 直接挂载
- 未安装 → 用内置 MSI 安装（要求进程为管理员）
- 已安装但版本不匹配 → 提示，不做降级/升级

`umafs winfsp` 可单独查看安装状态。

### 缺失数据

清单有 376,048 条，本机 dat 里实际有 370,143 条。差额 5,906 条来自两组：

- `DeleteOnLogin`（4,464 条）——登录时删除的资源，本地本就不该存在
- `DelayRelease` 等（约 1,400 条）——尚未发布的资源，CDN 上也返回 `NoSuchKey`

这些条目在挂载时**默认不列出**，因为 AssetStudio 遍历到不存在的文件会直接抛
`FileNotFoundException`。需要看到它们用 `--show-missing`；`umafs check` 可按分组列出全部缺失。

挂载后的结构就是清单里的路径：

```
U:\chara\chr1001\chr_icon_1001.unity3d   UnityFS 资源包
U:\sound\b\snd_bgm_am001.awb            CriWare 音频
U:\movie\m\...\*.usm                    视频
U:\_manifests\                          顶层清单
U:\_master\master.mdb                   明文 SQLite
```

## 构建

```
dotnet publish src/UmaFs.App -c Release -o publish    # umafs.exe（CLI + 挂载）
dotnet publish src/UmaDL  -c Release -o publish2      # umadl.exe（下载器）
```

需要 .NET 8 SDK。产物是单个自包含 exe（约 39 MB，内含 .NET 运行时、SQLite3MC 原生库与 WinFsp 安装包）。

## 项目结构

```
src/UmamusumeData/     清单读取层，取自 MarshmallowAndroid/UmaMusumeExplorer，未改动
src/UmaDL.Core/        云端清单展开 + 并发下载 + xxHash64 校验（无第三方依赖除 LZ4/xxHash）
src/UmaDL/             独立下载器 umadl（WinForms GUI + CLI）
src/UmaFs.Core/        清单索引、资源解密流、配置
src/UmaFs.Assets/      AB 包解析与解包
src/UmaFs.App/         CLI 与 WinFsp 挂载
vendor/AssetsTools.NET nesrak1/AssetsTools.NET（MIT），只用其 bundle 解析与纹理解码
```

## 技术要点

- **meta 加密**：SQLite3MC + chacha20，密钥内置，三组候选密钥逐个试
- **AB 解密**：前 256 字节明文，之后与 88 字节密钥流异或，取模用**文件绝对偏移**
- **单文件打包**：必须 `IncludeAllContentForSelfExtract`；winfsp 的 `CheckVersion` 用
  `Assembly.Location` 取自身版本，单文件不解包时该值为空会抛 `ArgumentException`
- **DLL 搜索路径**：新版 WinFsp 只写注册表 `SxsDir` 不写 `InstallDir`，而托管层回退逻辑只认
  `InstallDir`，需要在首次使用 Fsp 类型前 `SetDllDirectoryW` 指向实际 SxS bin 目录
- **目录枚举**：`ReadDirectoryEntry` 的 marker 必须对所有条目（含 `.`/`..`）生效，否则大目录会死循环


## 下载器 umadl（GUI + CLI，独立可执行文件）

`umadl.exe` 是独立下载器，**不依赖本地游戏**，也不需要 WinFsp。双击开 GUI，带参数走 CLI。

```
umadl                        GUI
umadl                        CLI 模式，默认下 DeleteOnLogin
umadl -o D:\uma -j 16        指定目录与并发
umadl --all --dry-run        先看全部需要多少
umadl -g DownloadLogin       指定分组
```

GUI 控件：分组下拉、并发数、目录选择、**代理**、检查清单 / 开始下载 / 停止、进度条、状态栏、日志区。

### 镜像 / 自建 CDN

```bash
umadl --cdn http://host:8080/dl/resources      # 资源与清单都从这个根取
umadl --root-api http://host/api/MetaC/root    # root 接口自己指定
```

镜像目录结构（与官方 CDN 对应）：

```
<cdn>/
  Manifest/{前2位}/{hash}              ← 清单（root / 平台 / 41 个分清单）
  Generic/{前2位}/{hash}               ← 带扩展名的资源（音频/视频/字体）
  Windows/assetbundles/{前2位}/{hash}  ← PC 资源包
  Android/assetbundles/{前2位}/{hash}  ← Android 资源包
```

`umadl` 下载时会把用到的清单**一并写入** `<输出目录>/Manifest/`，这样镜像自给自足。

### 密钥从哪来

清单每条资源的第 7 个字段（8 字节大端）就是解密 key。游戏解析后写进
`meta` 数据库 `a` 表的 `e` 列。资源包本身**不含**密钥 —— 只抓资源包拿不到 key。

### 平台切换

默认下 PC（Windows）版资源。Android 与 PC 的清单和资源包都不同，需要显式指定：

```bash
umadl --platform android          # 或 apk / mobile
umadl --platform ios
umadl --platform pc               # 默认，也可写 win / windows
```

GUI 第一行有「平台」下拉框（PC / Android / iOS）。

资源包路径随之切换：`/dl/resources/{Windows|Android|iOS}/assetbundles/`；
带扩展名的资源（音频、视频、字体等）走 `/dl/resources/Generic/`，与平台无关。

### 代理

支持 HTTP 代理（`http://` 也接受裸 `host:port`）：

```bash
umadl -p 127.0.0.1:7890        # 显式指定
umadl -p off                   # 强制直连
```

不指定时读 `HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY` 环境变量。GUI 会自动填入环境里的代理，
勾选「使用代理」后生效，「测试」按钮同时探测 root API 与 CDN。

**注意**：代理出口常被 Akamai 对 CDN 拦成 403（root API 却可能正常）。「测试」会分别报告两项，
只有 CDN 也通才能下载。这种代理下需要换代理或改用直连。

`umafs fetch` 是同一套实现（共用 `UmaDL.Core`），适合脚本化；`umadl` 适合手动操作。

### umafs fetch

不需要完整游戏数据，直接从云端清单拉取。清单流程与 UmaViewer standalone / 游戏客户端一致：

```
api/MetaC/root  ->  root 清单  ->  平台清单(Windows)  ->  41 个分清单  ->  376,012 条资源
```

```bash
umafs fetch                      # 下载 DeleteOnLogin 分组（登录即删、本地永远拿不到）
umafs fetch --all                # 下载全部缺失资源
umafs fetch -g DownloadLogin     # 指定分组
umafs fetch -n 100 --dry-run     # 先看会下多少
umafs fetch -o D:\uma -j 16      # 指定目录与并发
```

**CDN 路由**（与游戏一致）：名称带扩展名走 `/dl/resources/Generic`，无扩展名走
`/dl/resources/Windows/assetbundles`。请求必须带 `UnityPlayer` UA，否则 Akamai 返回 403。

**校验**：清单的 8 字节 checksum 是内容的 **xxHash64 大端**，下载后逐条比对，不符则废弃。

**默认目标** `%LOCALAPPDATA%\UmaFs\assets\<hash前2>\<hash>`，不写入游戏目录（避免破坏安装）。

各分组的实测规模（本机清单）：

| 分组 | 条数 | 说明 |
|---|---|---|
| `DownloadLogin` | 273,131 | 登录时下载 |
| `(none)` | 67,450 | 常驻 |
| `DeleteOnLogin` | **4,517** | 登录时删除，本地拿不到，7.45 GB |
| `DelayRelease` | 629 | 未发布，CDN 也返回 404 |

## 第三方组件

| 组件 | 许可 |
|---|---|
| [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) | MIT |
| [winfsp.net](https://github.com/winfsp/winfsp) | GPLv3 + FLOSS 例外 |
| WinFsp 安装包（内嵌，未修改） | GPLv3 + 例外，明确允许分发未修改的官方安装包 |
| [SQLite3MultipleCiphers](https://github.com/utelle/SQLite3MultipleCiphers) | MIT |
| [sqlite-net](https://github.com/praeclarum/sqlite-net) | MIT |
| [ImageSharp](https://github.com/SixLabors/ImageSharp) | Apache-2.0 |

## 免责声明

需要使用本机已安装的正式游戏数据。资源版权归 Cygames, Inc. 所有，
本工具仅用于本地浏览与导出，不包含也不分发任何游戏资源。