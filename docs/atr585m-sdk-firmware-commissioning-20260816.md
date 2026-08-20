# ATR585M SDK / FPGA 实机调试记录（2026-08-16）

## 结论

ATR585M 的连续曝光错位不是 N.I.N.A. 独有问题。旧 FPGA 4.50 在宿主软件逐帧重复设置全画幅 ROI 时，能够从第二帧开始产生精确的 64 pixel 横向错位；N.I.N.A. 和 ToupSky 都使用 ToupCam 底层接口，因此两个上层程序都可能触发同一种相机端状态错误。

相机已使用图谱官方工具由 FPGA 4.50 升级至 FPGA 5.0。N.I.N.A. 使用 ToupCam SDK 60.32226.20260808；ToupSky 4.12.30701 保持使用其安装包配套的 SDK 59.30701.20260128。升级后，三条独立路径均未再复现错位：

| 验证路径 | 宿主/SDK | 测试条件 | 第 2、3 帧相对位移 | 结果 |
| --- | --- | --- | --- | --- |
| 直接 SDK | SDK 60.32226.20260808 | 每帧强制全画幅 ROI reset | 0 px、0 px | PASS |
| N.I.N.A. 真实驱动 | 正式安装的 N.I.N.A. 3.2.0.9001 + SDK 60；未修改 `NINA.Equipment.dll` | 连续 3 张 1 s 平场 | 1 px、0 px；相关 0.994/0.994 | PASS，无 64 px 错位 |
| ToupSky 旧 SDK 复核 | ToupSky 自带 SDK 59.30701.20260128 | 每帧强制全画幅 ROI reset | 0 px、0 px | PASS |

因此当前证据支持：**FPGA 5.0 是消除该故障的关键修复，SDK 60 是同时采用的兼容性更新。** 不能仅凭现有测试精确区分图谱在 FPGA 5.0 内修改了哪一个寄存器或缓存状态；这需要厂商变更记录或固件源码。项目仍保留历史帧的 64 pixel wrap 检测/修复作为防护，但新数据不应依赖软件修复才能使用。

## 官方资料与升级方法

- 图谱天文官方固件页：<https://www.touptek-astro.com/Firmware/>
- 图谱官方 SDK 下载中心：<https://www.touptekphotonics.com/download/?category=SDK>
- 图谱官方 ToupSky 下载页：<https://www.touptek-astro.com/downloads/?atfWidgetNav=box_win>
- 图谱官方固件更新 FAQ：<https://www.touptekphotonics.com/FAQ/13>

官方固件页将该包标为 `ATR585 Firmware (FPGA v5.0 | Mono & Color)`，明确说明 ATR585M 与 ATR585C 使用统一固件，并要求升级后使用最新驱动、升级过程中不得断电。下载文件名虽然是 `ATR585C_FPGA5.0.zip`，但官方页面明确将它同时用于单色 ATR585M。

本机实际执行步骤如下：

1. 关闭持有相机的 N.I.N.A./ToupSky 等程序。
2. 从官方固件页下载 ATR585 FPGA 5.0 包。
3. 运行包内官方 `updatefw.exe`，明确选择 `ATR585M`，再选择包内 `ATR585-Firmware-V5.0.ufw`。
4. 在确认页核对目标为 `FPGA 4.50 -> 5.0`，保持 USB 与相机电源不间断，等待擦除、写入和回读完成。
5. 重新连接相机，分别通过新 SDK 和 ToupSky 自带 SDK读取版本；两条路径均回读 `FPGA 5.0`。
6. 用平场板做连续帧与重复 ROI reset 回归测试，而不是只确认“相机能连接”。

## 已记录版本与校验值

升级前相机：

- Hardware: `4.0`
- Firmware: `4.1.2.20240730`
- FPGA: `4.50`

升级后相机：

- Hardware: `4.0`
- Firmware: `4.1.2.20240730`
- FPGA: `5.0`
- MCU（官方更新器完成页）: `4.1.1.20241227`

下载包与关键文件：

- `ATR585C_FPGA5.0.zip`: SHA-256 `31A574A7B9D127EC3BD7F5156DF25306F5C1A0FFCA816FBA77DB5F8023B52F4B`
- `ATR585-Firmware-V5.0.ufw`: SHA-256 `C229094C2FA22AD343677EA5CE1466ABEFA34D4149B8AA9FDB123451EAFD1895`
- `toupcamsdk.20260808.zip`: SHA-256 `70F469B5A03F8FCD6216F0C1BF158BDF8731A76D63C9E5C1A43EECB543A61456`
- SDK 60 x64 `toupcam.dll`: version `60.32226.20260808`, SHA-256 `6CDD80DB1BA66E4F01A3EE2228828CC477A3D3A3130FE7B291922EBD7E96C9E8`

## 本机部署与回滚

ToupCam SDK 必须按**宿主程序**隔离管理，不能把“单一正式版本”误解为把同一份
`toupcam.dll` 覆盖到所有程序目录。当前经实机验证的组合为：

- N.I.N.A. x64：`C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\External\x64\ToupTek\toupcam.dll`，版本 `60.32226.20260808`，SHA-256 `6CDD80DB1BA66E4F01A3EE2228828CC477A3D3A3130FE7B291922EBD7E96C9E8`。
- ToupSky 4.12.30701 x64：`C:\Program Files\ToupTek\ToupSky\x64\toupcam.dll`，版本 `59.30701.20260128`，SHA-256 `719D97B25F4BCF72BB1D0DB49466A0F336A9699D7E50A887D12D96EB1CBFE715`。

2026-08-20 复盘确认：把 SDK 60 手工覆盖进 ToupSky 4.12.30701 后，ToupSky 在连接
ATR585M 时以 `0xc0000005` 原生访问冲突退出；故障模块虽显示为 `toupsky.exe`，进程当时
实际加载的是被跨代替换的 `toupcam.dll`。使用同版官方 ToupSky 安装包原位修复、恢复
SDK 59 后，ATR585M 自动连接并连续实时取流超过 2 分钟，进程保持响应，Windows
Application 日志新增崩溃事件为 0。N.I.N.A. 仍保留 SDK 60，并在 ToupSky 正常退出后
成功启动和实例化 UVEX 主控台。

仓库的 `scripts/update-touptek-sdk.ps1` 因此只允许更新 N.I.N.A. 自己的 x64 SDK，
不再触碰 ToupSky 目录。ToupSky 必须使用与其可执行文件同一安装包执行修复或升级；
禁止在两个宿主之间复制 DLL。相机 FPGA 不建议自行降级；如确需降级，应向图谱支持
索取匹配固件与操作说明。

## 原始验证数据

- SDK 60 + FPGA 5.0 + 强制 ROI reset：`output/commissioning/2026-08-16-daylight/raw/direct-sdk60-fpga50-roi-reset/`
- 正式 N.I.N.A. 路径、原版驱动 + SDK 60 + FPGA 5.0：`output/commissioning/2026-08-16-daylight/raw/nina-formal-sdk60-fpga50/`
- ToupSky SDK 59 + FPGA 5.0 + 强制 ROI reset：`output/commissioning/2026-08-16-daylight/raw/toupsky-sdk59-fpga50-roi-reset/`

每个目录保存三张原始 FITS；对应 JSON 保存分析行范围、帧统计、互相关位移和相关系数。

## 天光闭环续测状态

2026-08-16 当天在天光尚可时已获得太阳连续谱与吸收结构，但完成固件/SDK回归后太阳高度已降至约地平线下 8 度。30 s 暮光帧仅比背景高约 10--40 ADU，达不到“至少三条可靠吸收线 + 闭环验证帧”的置信度门槛。因此本次没有移动 UVEX 的 M2、光栅或狭缝，也没有为追求一次形式上的成功而放宽安全阈值。

对较早的 0.1 s 天光数据做了离线影子分析。当前插件的 `±12 px` 窗口在临时像素种子 `1583,2765,3514` 上分别得到约 `SNR 14.4/19.9/58.0` 与 `FWHM 6.94/9.42/9.52 px`，证明吸收线方向的度量链路可以工作。不过这些帧拍摄于 FPGA 升级前，并经过历史 wrap 修复；其 ISIS `p_g2v.dat` 模板相关仅 `0.231`，低于 `0.35` 验收门槛，所以这些数字只能作为下一次自动重搜的起点，不能写成最终波长标定或直接开放电机运动。诊断图和机器可读判定保存在：

- `output/commissioning/2026-08-16-daylight/diagnostics/daylight_absorption_shadow_analysis.png`
- `output/commissioning/2026-08-16-daylight/diagnostics/daylight_absorption_shadow_analysis.json`

下次太阳高度足够时，从以下状态继续：

1. 使用桌面的正式 `N.I.N.A.` 连接 ATR585M，gain 100、offset 256、1x1，直接制冷到 -10 C。
2. 先采 3 张相同曝光的天光帧，确认位移为 0 pixel、无 x=64 接缝。
3. 在插件影子模式确认至少三条未饱和 Fraunhofer 吸收线的 SNR、质心和 FWHM；吸收线度量使用负峰方向。
4. 只有质量门通过后才开放有界 M2 七点扫描；验证帧失败时退回初始 M2 位置。
5. 波长闭环继续使用独立的局部步数/像素标定，不能把粗略太阳线表直接当作最终 `gratingStepsPerPixel`。

本次结束时确认：Gemini 盖板 `Closed`、平场灯 `Off/0`、赤道仪 `AtHome` 且未跟踪/未转动、ATR585M 已重新连接并稳定在 -10 C。UVEX 机构在整个固件/SDK测试中没有运动。
