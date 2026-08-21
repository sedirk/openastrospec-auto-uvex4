# OpenAstroSpec Auto — UVEX4

[English](README.md) | **简体中文**

[![CI](https://github.com/sedirk/openastrospec-auto-uvex4/actions/workflows/ci.yml/badge.svg)](https://github.com/sedirk/openastrospec-auto-uvex4/actions/workflows/ci.yml)
[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D4.svg)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)
![N.I.N.A. 3.2](https://img.shields.io/badge/N.I.N.A.-3.2-4051B5.svg)
![Status: engineering preview](https://img.shields.io/badge/status-engineering%20preview-orange.svg)

OpenAstroSpec 是一个开源天文光谱项目家族。本仓库包含 **OpenAstroSpec Auto — UVEX4**：以 UVEX4 为首个完整支持光谱仪实现的自动观测与观测站编排产品，以及配套的离线光谱处理工作台 Spectral Studio。

> [!IMPORTANT]
> 本仓库目前是处于真实硬件 commissioning 阶段的**工程预览版**，不是通用的无人值守天文台安全认证产品。系统默认从模拟和 fail-closed 模式启动。在完成 commissioning 文档规定的站点证据与全部安全硬门之前，真实运动、导星、采集和无人值守科学观测均不可用。

本项目为 UVEX4 地基光谱仪提供开源的 N.I.N.A. 自动观测、设备单一所有权编排、可审计的目标入缝/导星闭环，以及完全离线的光谱处理工作台。默认配置只运行模拟器；真实设备控制必须先完成本机 commissioning、设备身份锁定和逐级安全验收。本项目与 NASA UVEX 空间任务无关，也不是 UVEX4 或 N.I.N.A. 官方发布。

**快速入口：** [观测控制产品](products/observatory/README.md) ·
[Spectral Studio](products/spectral-studio/README.md) ·
[构建与模拟器](#构建并运行模拟器) ·
[真实硬件 commissioning](docs/commissioning.md) ·
[操作员 SOP](docs/observatory-automation-sop.md) ·
[已知问题](docs/known-issues.md) ·
[参与贡献](CONTRIBUTING.md)

本仓库包含两个面向用户、均采用 GPL-3.0-only 许可的软件产品：

| 产品 | 用途 | 起点 |
|---|---|---|
| **OpenAstroSpec Auto — UVEX4（前期观测控制）** | N.I.N.A. 工作流、设备所有权与编排、commissioning、QHY/G3/ATR 实时诊断及不可变运行证据 | [`products/observatory/README.md`](products/observatory/README.md) |
| **OpenAstroSpec Spectral Studio — UVEX4（后期光谱处理）** | 离线 FITS 检查与处理、二维/一维可视化、波长/响应校准及成品交付 | [`products/spectral-studio/README.md`](products/spectral-studio/README.md) |

![星空下的 OpenAstroSpec Auto — UVEX4 观测站](docs/assets/openastrospec-observatory-night.jpg)

_OpenAstroSpec Auto — UVEX4 的开发与 commissioning 实景环境。本照片用于说明项目的物理背景，不代表该站点已经通过无人值守运行所需的安全硬门。_

![OpenAstroSpec Auto 模拟 N.I.N.A. 自动观测面板](docs/assets/openastrospec-auto-running.png)

_上图由离线 UI 测试工具生成，不包含真实设备状态，也不会连接观测站硬件。_

两个产品共享源码历史和科学数据契约，但不共享硬件控制权。Spectral Studio 严格离线。观测控制产品以单一所有权 Windows 服务、独立 WPF 管理器和 N.I.N.A. 3.2 插件替代 DRIVER.UVEX4 的日常控制部分。

本项目是独立的社区软件，不是 UVEX4 项目或 N.I.N.A. 的官方发行版。这里支持的是地基 UVEX4 光谱仪，与 NASA 的 UVEX 空间任务无关。正式公开名称以及为兼容性而保留的 `UVEX-ADV` / `UvexAdv.*` 内部标识，见[品牌与名称迁移策略](docs/project-branding-and-name-migration.md)。

原创源码采用 [`GPL-3.0-only`](LICENSE) 许可。独立应用、驱动、厂商 SDK、科学软件包和观测数据继续遵循各自条款，详见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。公开发布前应完成[开源发布检查清单](docs/open-source-release-checklist.md)；当前操作与发布限制记录在[已知问题](docs/known-issues.md)中。

## 权威采集设计

下一阶段采集自动化的冻结设计基线位于 [`docs/design/observatory-automation-baseline.md`](docs/design/observatory-automation-baseline.md)。各设备的单一所有权决策记录在 [`ADR-0001`](docs/adr/0001-single-owner-device-orchestration.md)，两个光学视场之间可选且版本化的交接规则记录在 [`ADR-0004`](docs/adr/0004-optional-versioned-wide-to-slit-field-transfer.md)。以下规则具有规范性：

- N.I.N.A. 独占 ATR585M，用于光谱采集；
- PHD2 独占 G3M2210M，用于狭缝视场与导星；
- 隔离的 `UvexAdv.Qhy.Service` 独占 QHYminiCam8M，用于 GS350 粗采集和同步测光；
- 只有 `UvexAdv.Service` 可以独占 UVEX4 COM5。

仓库已包含 QHY 服务、PHD2 事件服务器客户端、目标采集状态机以及 N.I.N.A. 真实/模拟运行器。健康阶段无需确认对话框即可自动推进；失败或不确定的安全门进入 `PausedNeedsAttention`。操作员始终可以暂停、恢复、取消或人工接管。必须先完成模拟 commissioning。构建和版本化 Git hook 会校验权威设计哈希，使意外架构修改明确失败。

当前 GS350/QHY 到 C11/G3 的光轴差绝不会作为编译期常量写入程序。未来可选预定位阶段只消费由操作员明确选择、带版本和来源的记录；记录必须绑定硬件/安装指纹、赤道仪侧、环境适用性、有效期、不确定度与运动限制。默认策略为 `AutoIfValidElseSkip`，界面也必须能明确选择 `Skip`。证据缺失或过期时，系统退化为 QHY 粗居中、G3 直接解析以及有界 G3 局部搜索，不会静默复用记忆中的偏移。

仓库贡献、数据隔离和冻结设计规则见 [`CONTRIBUTING.md`](CONTRIBUTING.md) 与 [`AGENTS.md`](AGENTS.md)。当前及规划中的组件地图见 [`docs/repository-layout.md`](docs/repository-layout.md)。

独立打包的 Python 后期处理产品提供 FITS 检查、ASPIRED 提取以及 Spectrum1D/FITS/CSV/PNG 成品，详见 [`reduction/README.md`](reduction/README.md)。它不会访问 COM5 或相机驱动。中文桌面 GUI 可通过 `OpenAstroSpec 光谱处理 - UVEX4` 快捷方式启动，开发环境也可使用 `Launch-UVEX-Spectrum-GUI.cmd`；当前支持使用本地 ISIS 恒星模板数据库进行质量门控的波长校准。已安装的快捷方式指向原生 `.NET 8` 启动器，与管理器快捷方式保持一致，不直接暴露命令文件或 Python 可执行文件。

处理后的光谱按 `reduction/output/目标/YYYY-MM-DD/` 组织。双击 `reduction/output/00-打开结果索引.html` 可打开可视化目录；历史运行和质量研究单独保存在 `reduction/output/_internal/`。

## 安全状态

- 仓库内配置使用 UVEX 模拟器，不会打开 COM5。
- 生产串口固定为 COM5、115200 8N1；打开前通过 Windows 设备注册表校验 `VID_1A86&PID_7523`。
- 电机命令还必须持有可续期的独占租约，并满足已配置的软件行程限制。
- 插件以 `Commissioned=false` 启动。光谱分析可用，但在 ROI、谱线、回差和限位测量完成前，闭环电机运动会被阻断。
- 项目有意不提供 EEPROM/网络写入、固件更新、盲扫串口或直接访问 ToupTek SDK 的功能。

## 组件

- `UvexAdv.Service`：仅回环地址 API（`http://127.0.0.1:47844`）、SignalR 遥测、Windows 服务托管、模拟/串口传输、操作审计数据库和滚动 JSON 日志。
- `UvexAdv.Admin`：中文独立管理器，仅通过服务 API 工作。
- `UvexAdv.Nina.Plugin`：N.I.N.A. 面板与高级序列项目。ATR585M 曝光通过 `IImagingMediator` 请求；本项目绝不自行打开 PHD2/G3M2210M。
- `UvexAdv.Qhy.Service` / `UvexAdv.Qhy.Core`：仅回环、单一所有权的 QHYminiCam8M 采集与测光服务，提供不可变 FITS/预览产品、帧指标、幂等任务、控制租约及模拟/回放适配器。
- `UvexAdv.Phd2`：严格的 PHD2 事件服务器客户端，负责 profile/设备/校准安全门、全帧证据采集、导星星选择、导星/settle，以及 Windows profile 绑定证据；它不会自行打开 G3M2210M。
- `UvexAdv.Observatory`：与设备无关的观测计划、地平线/运动安全门、自动阶段协调器、协作式暂停/恢复/取消/接管、狭缝视场分析、commissioning 变换和观测清单。
- `UvexAdv.Reduction.Launcher`：隔离 Python 光谱处理工作台的原生桌面入口。
- `UvexAdv.Spectroscopy`：托管的光谱提取、谱线质心/FWHM、焦点曲线拟合和波长闭环引擎。
- `UvexAdv.Protocol` / `UvexAdv.Core`：有文档的 UVEX 协议、增量解析器、租约、状态机、运动限制与停止行为。

## 构建并运行模拟器

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
.\artifacts\service\UvexAdv.Service.exe
.\artifacts\qhy-service\UvexAdv.Qhy.Service.exe
.\artifacts\admin\UvexAdv.Admin.exe
```

构建会发布 N.I.N.A. 插件，但不会自动安装。请先关闭 N.I.N.A.，安装插件，再重启 N.I.N.A.：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-nina-plugin.ps1
```

为兼容旧配置，插件仍安装到 `%LOCALAPPDATA%\NINA\Plugins\3.0.0\UVEX-ADV Spectroscopy`，但在界面中显示为 `OpenAstroSpec Auto — UVEX4`，包含 `OpenAstroSpec 自动观测` 和 `OpenAstroSpec 校准库` 两个面板，以及 `OpenAstroSpec Auto` 高级序列分类。ATR585M 身份绑定和单帧提取检查已并入 `OpenAstroSpec 自动观测 → 实时图像 → ATR 二维/一维光谱`，不再保留独立的占位“光谱”页。文件夹名与插件 GUID 保持不变，因此已有 N.I.N.A. profile 会继续加载同一个插件。

QHY 服务单独安装，默认使用合成模拟器：

```powershell
# 在 scripts\build.ps1 完成后，从管理员 PowerShell 运行。
powershell -ExecutionPolicy Bypass -File .\scripts\install-qhy-service.ps1
```

`-EnableHardware -HardwareConfigurationPath <machine-local-qhy-json>` 会锁定并复制本机已安装的 x64 QHY SDK，校验其版本化 SHA-256，要求显式机器本地文件中提供完全匹配且已 commissioning 的 QHY 稳定身份，并且仍只公开 `http://127.0.0.1:47845`。仓库内 `config/qhy.production.json` 是有意设计为不可直接运行的示例。在模拟器、锁定的 Night Setup、PHD2 profile 证据、狭缝几何、天文解析和有界像素到赤道仪变换全部通过 commissioning 前，不要启用真实硬件。

从示例创建被 Git 忽略的 `config/qhy.local.json`，替换全部占位符，再明确选择此机器本地配置：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-qhy-service.ps1 `
  -EnableHardware -HardwareConfigurationPath .\config\qhy.local.json
```

## 自动目标观测

面向操作员的中文 SOP 位于 [`docs/observatory-automation-sop.md`](docs/observatory-automation-sop.md)，覆盖 Night Setup、校准夹逼、40 度地平线硬门、实时预览、暂停/恢复/取消/接管、证据复核、故障恢复，以及在 Dome/Roof 和 Safety Monitor 输入尚不可用时的监督式 commissioning 限制。

长期问题清单、夜间主题规则、截图矩阵和操作员 UI 验收记录位于 [`docs/observation-operator-ui-acceptance.md`](docs/observation-operator-ui-acceptance.md)。隔离的 WPF 测试工具使用确定性模拟状态渲染真实生产数据模板；它不会构造生产 dockable，也不会联系设备。

`OpenAstroSpec 自动观测` 面板首先提供两个明确选项：**模拟演练（不连接任何真实设备）**和**真实设备控制（必须通过全部安全硬门）**，之后只有一个理解当前模式的启动按钮。仅仅选择真实模式不会连接或移动设备。QHY/GS350、PHD2/G3 狭缝视场和 ATR 光谱预览被组织为紧凑标签页，并显示当前阶段、下一阶段、质量门、时间线、证据文件和剩余进度。持久故障卡会把最近失败的安全门及指标链接到对应的可缩放/平移预览和证据目录。

目标草稿可以从 N.I.N.A. 构图助手，或 N.I.N.A. 已配置星图软件（包括 Stellarium）当前选定对象中一次性复制。插件把快照规范化为 J2000 度，并记录来源与时间。导入不会连接设备或启动运行，不会更改 Night Setup、commissioning、时长或安全设置，也不会改写已经创建的高级序列容器。在实现面板选择流程前，多画幅构图会被拒绝。

一次正常运行包括：

1. 校验不可变 Night Setup、schema-3 commissioning preset、准确设备身份、环境、覆盖最坏剩余时长的目标地平线、UVEX 位置以及版本化 PHD2 校准质量策略。系统会对当前 active PHD2 校准进行分级，而不是只使用一个正交误差阈值；退化运行和直接目标星导星只能在明确的监督模式下使用。
2. 转到目录坐标，采集并解析 QHY 广域 FITS，每次有界有符号修正后都重新解析。可以选择应用单独 commissioning 的广域到狭缝视场预定位记录；记录无效或被显式跳过时，不会产生中间运动。
3. 通过 PHD2 和已 commissioning 的长曝光阶梯采集新鲜 G3 狭缝视场。若 WCS 成功但目标仍在探测器外，系统由 N.I.N.A. 执行有界居中并重新解析；至少包含一个相干源的结构化稀疏视场可进入已配置的小步搜索。零源、云层或透明度不确定的视场绝不授权运动。可选的已校准鬼影匹配可以估计亮目标质心，但不能单独确立目标身份或授权运动。测得目标和狭缝后，首选精细运动权威是当前合格或显式受监督的 PHD2 校准加分段 exact-lock 位移；独立 commissioning 的 G3 像素到赤道仪变换作为后备。普通路径先从新的秒级曝光中选择导星星；只有显式退化路径才在单独 commissioning 的短曝光下直接导超亮目标星。每一阶段都必须 settle，并在新的不可变 G3 帧中重新测量。
4. 启动带租约的 QHY 测光，探测 ATR 光谱 ROI，选择安全的离散曝光档位，通过 N.I.N.A. 保存经过质量门的科学帧，最后仅在 QHY 与 PHD2 终态都已验证后完成运行。

常规流程没有“是否继续？”确认门。`Pause` 会在下一次有界运动或曝光前生效；`Resume` 会重新校验过期安全门；`Cancel` 执行经过验证的清理；`Take over` 释放协调所有权并保持运行暂停。

## ATR585M 暗场/偏置库

在 N.I.N.A. 中打开 `OpenAstroSpec 校准库` 面板。它通过 N.I.N.A. 的 `IImagingMediator` 采集，保存原始 16 位 FITS，写入带 SHA-256 的可恢复 JSON 会话清单；同组至少五帧时可构建流式的“去掉一个最高值和一个最低值”截尾均值 master。它绝不自行加载 ToupTek SDK。

目录键包含相机身份、增益、偏置、binning、读出模式、温度、帧类型和曝光时间。已 commissioning 的 ATR585M 默认值为 gain 100、offset 256、1x1、High Conversion Gain 和 -10 °C。它们必须与科学帧一致；连接相机或读出模式发生变化时插件会停止。Master dark 有意保留相机偏置基座，并以 `DARKBIAS = T` 标记；下游处理不应再次减 master bias，除非先把 dark master 转换为暗电流。

ATR585M 报告没有机械快门，因此每次运行都必须重新、且不持久化地确认镜盖和屋顶已关闭。对于经过确认的无人值守观测站运行，应在启动或重新连接 N.I.N.A. 前提交同样受保护的请求：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\request-calibration-library.ps1 `
  -ConfirmDarkness -CameraId '<commissioned-atr585m-device-id>' `
  -BiasFrameCount 32 -DarkFrameCountEach 5
```

请求包含精确 ATR585M DeviceId，并在四小时后过期。只有该相机已连接且空闲时插件才会接受；它不会按设备列表位置选择相机。原始文件默认保存到 `%USERPROFILE%\Documents\UVEX-ADV Calibration Library`。示例中的 `<commissioned-atr585m-device-id>` 必须替换为当前机器 N.I.N.A. profile 中的精确值；脚本不提供硬件身份默认值。

ATR585M offset 决策和已废弃的初始种子运行记录在 [`docs/calibration-library-seed-20260816.md`](docs/calibration-library-seed-20260816.md)。gain-100/offset-16 校准树已被有意删除，不应复用或重建。该相机新的 bias、dark、flat 和 science 帧应使用 offset 256，以确保兼容性键一致。

ATR585M 已于 2026-08-16 使用官方 FPGA 5.0 固件和 ToupCam SDK 60 完成 commissioning。三组独立的重复 ROI 测试——直接 SDK、未经修改的 N.I.N.A. 3.2 设备驱动以及 ToupSky 自带旧 SDK——在升级后都测得帧间位移为零。校验和、原始 FITS、回滚路径，以及为何同一个历史故障同时出现在 ToupSky 和 N.I.N.A. 中，记录在 [`docs/atr585m-sdk-firmware-commissioning-20260816.md`](docs/atr585m-sdk-firmware-commissioning-20260816.md)。

本地 SDK 被 Git 有意忽略。如果 `.dotnet` 不存在，请安装 .NET 8 SDK 8.0.419，或更新 `global.json` 并重新验证 N.I.N.A. 兼容性。

`scripts/install-service.ps1` 默认安装模拟模式，只有显式提供 `-EnableHardware` 才会启用硬件。已有 `%ProgramData%\UVEX-ADV\config.json` 始终会保留。

在已 commissioning 的 COM5 计算机上，先关闭 N.I.N.A.，再双击一次 `Install-UVEX-ADV-Hardware.cmd` 并接受管理员提示。为兼容性保留了旧脚本文件名。安装器会构建并测试项目，安装两个自动启动的 Windows 服务和 N.I.N.A. 插件，并创建 `OpenAstroSpec Auto - UVEX4 Manager` 桌面快捷方式。如果 N.I.N.A. 仍在运行，安装器会在修改任何服务前停止，以免插件与服务版本不一致。插件安装器还会拒绝缺少观测、PHD2 或 QHY 模块的过期发布输出。安装后不要手工启动服务可执行文件，只应打开桌面快捷方式。

## 真实硬件 commissioning

在完成 [`docs/commissioning.md`](docs/commissioning.md) 的全部步骤前，不要设置 `Simulator=false`。DRIVER.UVEX4 与旧名称的 `UVEX-ADV` Windows 服务绝不能同时占用 COM5。

协议实现仅基于公开的 [UVEX4 串口协议](https://spectro-uvex.tech/wp-content/uploads/2022/02/spec-driver-spectro.pdf)。该文档中重复的 `FSTE` 条目被视为歧义；本项目使用无歧义的相对对焦命令 `FGIN`、`FGOU`、`FHOM` 和 `FSTP`。
