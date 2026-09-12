# 不规则星像检测：SEP 基座、N.I.N.A. 接口与离线验证

2026-09-13。当前源码 `.191`，安装 `.190`；最新人工一键多目标记录见
[本夜收口](closeout-2026-09-13.md)，新增主镜对焦入口及其未安装边界见
[主镜对焦说明](sep-main-mirror-focus-commissioning.md)。下文选型/初次接入记录保留其当时验收范围。
`.187` 修复了 SEP 分源子光斑的偶然目录配对，并增加标准目录坐标口径复核，
详见 [完整父区域与标准坐标修复](sep-catalog-primary-2026-09-13.md)。`.186` 已将 SEP 接入真实生产 runner 的 **WCS 长帧饱和后的短帧位置复核**，
不再调用该分支的自制轮廓检测器；N.I.N.A. 的通用 SEP 检测选项仍由用户选择，没有改 Profile 默认值。
此次不是将所有 G3 精调/残差分支一次性替换，也不是已经完成天空闭环。详见
[SEP 生产短帧接入与验收](sep-production-short-position-2026-09-13.md)。
首次 `.184` 安装只含双星配对，第二次 `.185` 还修正连续帧分量身份传递，见
[连续帧修复](almach-component-lineage-2026-09-13.md)。

## 结论与选型

用 **SEP 1.4.1** 作为背景估计、连通区域提取、分源、矩、孔径通量和包围能量的基座，
本项目只加入测量用途、父区域/子分量保留、原始像素支持、异常标记及 N.I.N.A. 适配。
不再以圆形 Gaussian、单个局部极大值或固定小窗口作为所有星像的前提。

| 候选 | 上游能力与本项目定位 | 本次实际验证范围 |
| --- | --- | --- |
| [SEP](https://github.com/sep-developers/sep) | 从 SExtractor 抽出的 C 测量库；无设备所有权，适合离线/服务共用 | 固定 1.4.1；真实 FITS 与合成帧；C#↔Python 完整链路 |
| [Hocus Focus](https://github.com/ghilios/hocus-focus) | 现成 N.I.N.A. 检测、注释、自动对焦；上游已有 donut-aware 设置 | 读源码与接口，未在本机天空/验证集上运行它，不能声称比它更准 |
| [Photutils](https://photutils.readthedocs.io/en/2.3.0/user_guide/segmentation.html) | 分割/测光工具链，适合独立交叉验证 | 2.3.0 DAOStarFinder 默认参数基线；不是 Photutils 全套分割的最优性能 |

Hocus Focus 的 [Donut-Aware 设置入口](https://ghilios.github.io/hocus-focus/settings/) 本身值得使用；
没有必要重写其完整对焦 UI/曲线拟合。选择 SEP 是为了得到小而可独立复放的**测量核心**，
不是宣称 Hocus Focus 不能处理异形星像。研究检查的 Hocus Focus 提交为
`e9fbd333e5113292d3d1e51869fb128e424749d6`，上游主线文档可能领先已安装发行版。

### 依赖和许可证记录

- SEP 上游声明 LGPL-3.0，部分文件另有 BSD/MIT 许可，见其仓库 `licenses` 目录及文件头。
  本仓库没有复制 SEP/Hocus Focus 实现，也未提交 wheel/native 二进制。
- Hocus Focus / N.I.N.A. 源码采用 MPL-2.0；本实现引用安装好的 N.I.N.A. 公共接口和 ROI 工具。
- 可选运行环境按 `scripts/star-detection/requirements.txt` 固定版本。从上游安装依赖时保留
  包的许可证与来源；未来若分发打包环境，需要一并提供其许可、通知和对应来源。
- Python 3.11 环境完全独立，**不得升级 `reduction/.venv` 的科学依赖**。

## 为什么要分开三件事

1. **检测到一个发光区域**：甜甜圈、羽毛球、三角形、栗子形、长条形都可返回测量。
2. **该测量可参与对焦**：记录 R50/R80、饱和、边界、混合分量、原始像素支持与信号质量。
   不圆不是拒绝理由；饱和或混合帧仍保存结果，只不冒充可靠的单星曲线点。
3. **该区域就是已选目标，且位置能授权入缝**：还需要正式 WCS/目录关联、狭缝照明证据、
   新帧一致性与设备/运动约束。SEP 输出永远声明 `target_identity_confirmed=false`、
   `motion_authorized=false`。空洞中心或光通量质心不能直接替代狭缝定位所需的位置定义。

Almach 是目录双星，但 SEP 分出的两个子区域不自动等于主星与伴星：同一不规则星像也会
分瓣。早期 `.184`–`.186` 曾使用目录伴星方向/间距配对，实际失败帧暴露了偶然匹配问题，
见[历史修复记录](almach-companion-recovery-2026-09-13.md)和
[.187 当前父区域/目录策略](sep-catalog-primary-2026-09-13.md)。不能把不可靠子光斑，
也不能把真正混合双星的整体质心，直接当作主星入缝位置。

## 测量方法

- 输入仅为未拉伸单色原始阵列；FITS 用 Astropy 处理 BZERO/BSCALE，再检查实际 ADU 范围。
  N.I.N.A. 适配器读取 `RawImageData.Data.FlatArray`，不读取画面截图、叠加文字或预览曲线。
- SEP 局部背景与噪声图；3.5σ、至少 5 像素区域。先提取不分源的父区域，再保留 SEP 的
  分源子分量。输出按父区域通量排序，最多 1000 个，记录总数和 `truncated`。
- 使用 SEP 原生孔径测光与 [flux_radius](https://sep.readthedocs.io/en/stable/api/sep.flux_radius.html)，
  R50 是孔径内净通量的 50% 包围半径，R80 为 80%；不是 Gaussian 拟合 FWHM。
  孔径半径为 `max(6, 4a)`、上限 128 px；非圆 PSF 也可积分，但有限孔径仍可能遗漏长翼。
- 不正向截断背景噪声、不修改原始帧。保留矩长短轴、方向、分源、饱和比例、孔径标志。
- 当前 SNR 是“孔径通量 / 背景误差”，未输入电子增益和源泊松项，**不是标定测光 SNR**。
  不把这个诊断数当作光谱科学帧信噪比或入缝目标身份门。
- focus-eligible 排除饱和、孔径越界/受限、混合分量、像素支持不足、低 SNR、非法能量半径。
  单点热像素即使卷积后扩成一个区域，也不能凭它生成有效对焦点。
- 没有按设备名称、目标名称或“圆度必须接近 1”筛掉异形星像。

## 已保存导星帧的验证

生成物在忽略目录 `output/star-detection-validation-20260913/`：

- `manifest.json`：20 张真实帧的相对路径和 SHA-256，可固定复放；包括历史关灯导星帧、
  γ Cas 的短帧/长帧失败记录，以及本次 Almach。仅读，不改写原始观测。
- `report.json`：旧 `StarFieldDetector`、SEP 默认分源、Photutils DAO 默认、SEP 父区域版的
  完整候选与本版测量。DAO 的 Gaussian FWHM 固定 4 px；各算法未做等量参数优化，不能据此排名。
- `synthetic.png`、`real-1.png`、`real-2.png`：可视对照。真实图以高通量区域为展示中心，
  **不代表已经由目录确认的目标**，也不是人工精确质心标签。

合成帧固定种子、已知注入光通量质心，六种形态各一张；下列不是实测天空精度：

| 形态 | 旧算法候选数 | 旧算法最近候选质心误差 px | SEP 父区域数 | SEP 质心误差 px |
| --- | ---: | ---: | ---: | ---: |
| 圆形 | 1 | 0.0958 | 1 | 0.0046 |
| 甜甜圈 | 19 | 9.6206 | 1 | 0.0057 |
| 三角形 | 3 | 2.6409 | 1 | 0.0045 |
| 羽毛球 | 2 | 2.4630 | 1 | 0.0157 |
| 栗子形 | 1 | 2.4322 | 1 | 0.0048 |
| 长条形 | 2 | 0.7483 | 1 | 0.0253 |

真实帧结果与限制：

- 所有 20 张读取前后 SHA-256 一致。核心 Python 测量约 0.31–0.86 秒/帧，中位约 0.51 秒；
  不含进程冷启动、N.I.N.A. 处理和 FITS I/O，不是稳定的实时延迟保证。
- 10 张触及 1000 父区域上限，很多是弱噪声/缺陷候选。经过用途标记，每帧仅 0–38 个
  focus-eligible 区域；这也**不是 0–38 颗经标注确认的真星**。默认参数不能直接当投产验收。
- 一颗亮星的多个峰/光翼由一个父区域保存，不再将每个局部峰当独立目标；饱和长曝光
  会记录为饱和/扩展，不用其光晕中心冒充短帧主星位置。
- Almach 短帧保留一个连通父区及两个分量，并标记混合；没有选择“最亮就一定是主目标”。
- 尚无独立人工真值标签、暗弱目标检出率/误报率、跨夜留出集及完整 C11 焦点扫描，
  不能报告真实准确率或“已找到最佳焦点”。参数调优和验收必须按整次运行/整夜分组，
  不把同一星场相邻帧一部分训练、一部分测试来虚增成绩。

## N.I.N.A. 检测方式选项

`SepStarDetection` 使用真正的 MEF `IPluggableBehavior` / `IStarDetection` 接口导出：
**OpenAstroSpec SEP 不规则星像（实验性）**。

- 仅在安装包含该类的 `.185` 或后续版本后才有此选项；`.184` 没有。
- 显式配置可选 Python 运行环境后，由用户在 N.I.N.A. 星点检测方式中选择。没有改 Profile
  默认值，没有自动替换 Hocus Focus。检测器配置缺失会给出清楚错误，不静默回退到另一算法。
- 尊重 N.I.N.A. 原有 ROI 和对焦亮星数量参数；给定参考星时做有界一对一匹配，缺星/歧义
  不返回偏置曲线点。没有参考星时不宣称跨帧追踪了同一星群。
- `AverageHFR` 为合格星的 R50 中位数，离散度为未缩放 MAD；空列表给 NaN，不给伪造零值。
  所有未合格测量仍在扩展 `SepStarDetectionResult` / `SepStarDetectionAnalysis` 中保留。
- Python 每次只接受一张原始阵列，通过 stdin/stdout 传输；最多 3200 万像素，60 秒期限，
  输出大小和 schema 校验，最多一个 worker。取消只结束自己创建的子进程，不停 N.I.N.A./PHD2。
- 不接受 Bayer 马赛克。N.I.N.A. 通用 sensitivity/noise-reduction 参数当前不映射到 SEP；
  SEP 参数固定并记录，避免界面显示一套、实际暗中用另一套。

环境安装（需先关闭 N.I.N.A.，不启动软件或连接设备；路径为本机 Python 3.11）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/star-detection/setup-runtime.ps1 -PythonPath C:\Path\To\Python311\python.exe
```

脚本在 `%LOCALAPPDATA%/UVEX-ADV/star-detection` 创建全新版本目录，保留旧配置，最后更新
`runtime.json`。不修改 reduction 环境。首次依赖下载需要网络；实际图像检测不需要网络。

## 主镜对焦：能复用什么，尚缺什么

**“测到更小、能量更集中的星像”比“变成圆形”更适合这套反射导星光路。**
先用 R50 的稳定低谷，辅以 R80、饱和和光通量监控。若尾翼随焦点变形，R50/R80 可能给出
不同低谷；最终应在狭缝方向的能量/实际光谱通量上复核。二维质心可能随着像差改变，
不能把对焦改善当作目标位移，更不能用同一个质心定义直接控制入缝。

N.I.N.A. 原生 AutoFocus 会调用其选择的 `IStarDetection` 测量当前 N.I.N.A. 相机图像，
所以本插件的 R50 结果可复用原有 STARHFR 曲线流程；但它不会因选择这个检测器就自动
把 PHD2 的光谱仪导星相机图像变成 N.I.N.A. 的主相机输入。

本项目必须继续保持：PHD2 唯一持有光谱仪导星相机，主 N.I.N.A. 持有 C11 电调焦和光谱相机，
测光 N.I.N.A. 只持有测光设备。**禁止为复用自动对焦而把 G3 同时连接到 N.I.N.A.。**

因此交付分界明确：测量核心与原生检测接口已经实现；“PHD2 fresh frame → 主 N.I.N.A.
有界 C11 扫焦 → 复用 N.I.N.A./Hocus Focus 曲线与回差流程”的完整对焦图像适配层仍待实现
和分阶段验收。最初的检测器交付没有新增设备所有者或运行电调焦。

2026-09-13 后续已在明确授权下完成 Star Focuser 主镜部件级扫焦：PHD2 原生新帧、N.I.N.A.
原生电调焦/回差、SEP 同星群固定孔径及实际 N.I.N.A. 双曲线拟合均已接通。
现场数据、命令及尚未完成的原生 AutoFocus 图像适配边界见
[SEP 主镜对焦实机调试](sep-main-mirror-focus-commissioning.md)。这不等于整个原生 AutoFocus 窗口已完成接管。

更早的 focus evidence 中的位置都是 `5000`，当时不能据此推导最佳焦点。
后续明确授权的实机扫焦已补充焦内/焦外、固定曝光增益、同一星群和往返验证数据，
实测仍保留5000，并未证明候选点具有重复优势。

`.191` 将上述能力落入 `自动准备 → 主镜 SEP 对焦`：可见按钮与既有后台 `start-main-focus`
共用执行器，原生N.I.N.A.运动/回差和拟合、PHD2新帧、SEP同星群R50/R80及结果保存已经接通。
21张实机保存帧通过新C#对焦协议，与原分析逐项一致。该轮只进行了代码和离线验证，
未安装/重启/移动设备；正式前台天空验收和完整原生AutoFocus窗口接管仍待完成。
详见[程序集成与验收边界](sep-main-mirror-focus-commissioning.md)。

## 复现检查

```powershell
# 使用独立环境；示例为本次已创建的本地开发环境
.\output\star-detection-venv\Scripts\python.exe -m pytest scripts/star-detection -q
.\.dotnet\dotnet.exe build tests/UvexAdv.StarDetection.Benchmark -c Release
$env:UVEX_SEP_TEST_DOTNET=(Resolve-Path .dotnet/dotnet.exe).Path
$env:UVEX_SEP_TEST_DLL=(Resolve-Path tests/UvexAdv.StarDetection.Benchmark/bin/Release/net8.0/UvexAdv.StarDetection.Benchmark.dll).Path
.\output\star-detection-venv\Scripts\python.exe -m pytest scripts/star-detection -q
.\output\star-detection-venv\Scripts\python.exe scripts/star-detection/benchmark.py --observations "$env:LOCALAPPDATA/UVEX-ADV/observations" --output output/star-detection-replay --manifest output/star-detection-validation-20260913/manifest.json --dotnet .dotnet/dotnet.exe --legacy-dll tests/UvexAdv.StarDetection.Benchmark/bin/Release/net8.0/UvexAdv.StarDetection.Benchmark.dll
```

新代码增加 C# 协议/取消/边界测试与 N.I.N.A. 导出、ROI、星群缺失、完整诊断保留测试。
Python 覆盖六种形态、热像素、空帧、非有限像素、饱和、边界、双星分量、协议截断/尾部字节、
输入不变，以及可显式运行的真实 C#→Python→C# 集成测试。生成数据和图不进入 Git。

本次验收记录：`.185` 完整构建 1689 个 .NET 测试通过；独立 Python 测试 11 个通过（包括
真实 C# 传输测试，没有跳过）。安装到 LocalAppData 的独立 worker 又通过一次 C# 实际调用：
甜甜圈返回一个区域，R50=10.3926 px，质心相对合成真值偏差 0.00568 px。
38 个离线 UI 场景已渲染并检查；真实 N.I.N.A. 内切换检测选项、原生对焦及三面板交互验收
尚未完成，当时 `.185` 前台观测仍使用原检测路径（`.186` 的变更见本文顶部）。首次 `.185` 新日志有主相机驱动
`Could not set TemperatureSetPoint to 1000`，不是 SEP 检测调用；不能把日志描述为零错误。
