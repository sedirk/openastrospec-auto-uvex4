# SEP 主镜对焦：程序入口与 Star Focuser 实机调试

## 程序集成（0.4.0.191，2026-09-13）

在用户确认赤道仪和屋顶已归位后，将前次部件级验证的逻辑集成到插件。
**本轮只修改源码并完成离线验证，没有安装、重启、曝光或移动任何设备；待前端实机验收。**
下文“本次实际数据”是集成前那次明确授权的现场调试，不是新按钮的天空验收记录。

### 从哪里使用

`OpenAstroSpec 自动观测 → 自动准备 → 主镜 SEP 对焦 · 不规则星像`，位于准备页顶部，默认折叠。
可保存软限位、采样步长、每侧焦位数、每焦位帧数、曝光、增益、传感器饱和值及最低高度，
参数保存在当前主 N.I.N.A. Profile。软限位必须按实际电调焦填写，不把本站5000或行程写成通用默认。
按钮为“保存对焦参数 / 开始主镜对焦 / 取消并安全回位 / 打开对焦记录”。
运行时显示焦位/帧数，完成后显示R50曲线、R80与有效帧表；加载的历史结果明确不是当前设备读回。

这是一项**观测前独立准备动作**，不自动运行整个观测计划，不打开屋顶/镜盖、不转向目标，
不启停别人正在运行的PHD2导星。启动前要求当前主控已连接并选定主镜电调焦、镜盖打开且灯关闭、
屋顶读回打开、赤道仪在安全高度保持跟踪、PHD2停止且持有绑定导星相机、没有科学曝光。
对焦与正式观测共享进程内运行锁和跨进程所有权锁；用户切换Profile会取消旧对焦，不把旧结果保存到新Profile。

### UI 与后台共用路径

| 部分 | 程序实现与边界 |
|---|---|
| 可见按钮 | `SepMainFocusViewModel.StartCommand` / `CancelCommand` |
| 既有后台控制接口 | `start-main-focus` / `cancel-main-focus` 调用同一命令；启动仍需现有本次会话真实授权，没有新的绕行端点 |
| 共享执行器 | `SepMainFocusRunner`，由 `ObservationCoordinatorHost.RunFocusPreparationAsync` 排他执行 |
| 主镜运动 | `NinaSepMainFocusHardware` 调用原生 `IFocuserMediator.MoveFocuser`，使用当前N.I.N.A.回差补偿，不直接连ASCOM |
| 图像 | PHD2原生 `capture_single_frame`，不让N.I.N.A.重复持有导星相机 |
| 测量 | `SepStarDetectionClient.MeasureFocusAsync` → 同一 `worker.py` / `focus_metrics.py`，进程仅接收图像数组，无设备接口 |
| 拟合 | 直接复用N.I.N.A. `HyperbolicFitting`，不是重新实现同名公式，也不调用误用光谱相机的AutoFocus VM |
| 后台状态 | 既有状态快照增加 `MainFocusBusy`、`MainFocusStatus`、`MainFocusEvidenceDirectory` |

新入口不调用历史实机脚本 `commission_focus.py`；脚本只保留用于明确授权的调试和复现。

### 如何选择焦位和保存结果

先读回本轮原位，取得至少3颗参考星，按原位两侧的对称焦位采样，再回原位拟合。
固定孔径SEP R50/R80比较同一批孤立星，父区域离焦后分瓣可以保留完整星光，
不以圆度筛掉甜甜圈、三角形或长条星像；但不能保证每一种复杂/重叠星像都有可靠测量。
无星、重叠、饱和、孔径出界或不足2张共同星群帧属于缺测，不记为零半径。

原生拟合R²须至少0.8且低谷位于实际采样区间内才进入候选验证；随后重新建立局部参考星群，
执行“原位—候选—原位—候选”验证。两次R50均改善至少5%，R80均不恶化超过10%，才保留候选。
没有重复优势则明确返回“保留原位”，**不是给正式观测增加对焦质量阻断门**。
位置、设备、回差或安全状态发生外部变化时停止新动作；取消/异常仅在状态仍可信时尝试回原位，
不覆盖人工操作，不把未确认的返回写成成功。默认总时限15分钟，单段最多150步；
整个扫描和原生Overshoot余量均须包含在软限位内。隐藏物理位置偏移的Absolute回差模型暂不支持。

每轮使用新的 `%LOCALAPPDATA%/UVEX-ADV/main-focus/<UTC-GUID>/` 目录，原始FIT不覆盖；保存计划、
逐步运动意图/读回、逐帧同星群测量、原生拟合、往返验证以及最终 `result.json`。
当前Profile只保存参数和最后结果路径，不把影像或机器配置写入Git，不改写旧Night Setup证据。
主镜变化后下一轮仍须重新取得目标和狭缝证据。

### 部署与验证状态

`.191`插件已构建到 `artifacts/nina-plugin/`，**尚未安装**。下次明确安排安装时，关闭两台N.I.N.A.实例，
同时更新独立SEP运行环境和插件；不能只复制新DLL却继续使用不支持对焦协议的旧worker：

```powershell
# 安装操作须另行安排；不是本轮已执行的命令。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/star-detection/setup-runtime.ps1 -PythonPath <Python-3.11解释器>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-nina-plugin.ps1
```

环境安装脚本现在同时打包 `focus_metrics.py`；运行前缺少该文件会给出明确安装提示。
没有修改独立后处理的 `reduction/.venv`。

本轮离线验证：

- 全量 `scripts/build.ps1` 成功：0编译警告/错误，1783个.NET测试通过，包含新增执行器与入口共用路径检查。
- SEP测试29个通过，含真实C#→Python协议和不规则星群平移追踪；reduction检查及66个测试通过。
- 之前真实10秒细扫21张FIT逐张通过新的C#→SEP对焦通道，与保存的分析相比，
  匹配星编号、位置、R50/R80、通量、SNR完全一致，最大数值差0，原始FIT内容不变。
- 全部40个离线UI场景已渲染检查，新增常规/窄窗口主镜对焦场景；
  安装后的AvalonDock实例化、实际Star Focuser运动、单次前台启动天空验收均尚未执行。

真实帧协议重放可复现为（仅图像输入，无硬件）：

```powershell
.\output\star-detection-venv\Scripts\python.exe scripts/star-detection/replay_focus_protocol.py `
  --baseline output/sep-main-focus-20260913/fine-analysis/replay.json `
  --dotnet .dotnet/dotnet.exe `
  --client tests/UvexAdv.StarDetection.Benchmark/bin/Release/net8.0/UvexAdv.StarDetection.Benchmark.dll `
  --output output/new-focus-protocol-replay
```

尚未做：把PHD2图像全面接入N.I.N.A.原生AutoFocus窗口、随Advanced Sequencer自动对焦、
狭缝方向能量或实际光谱通量最优验证。当前复用的是原生运动/回差和曲线拟合，不夸大为完整原生窗口接管。

## 历史部件级工具与权限边界

本工具是**显式授权后的部件级扫焦工具**，不是自动观测的另一个启动入口，也不是已经完成的
N.I.N.A. 原生 AutoFocus 图像适配器。必须在自动观测结束、PHD2 停止、无科学曝光时使用。
不能拿它绕过运行中的序列、设备所有权或安全状态；每次现场操作仍需当次授权。

真实路径：

1. `commission_focus.py` 通过本机 N.I.N.A. Advanced API 调用 `MoveFocuser`。
   Star Focuser 仍由光谱主 N.I.N.A. 持有；动作显示在其电调焦面板，并使用它现有的回差补偿。
2. `UvexAdv.StarDetection.FocusHost capture` 调用本项目 `Phd2Client` 的原生
   `capture_single_frame`，严格核对当前 Profile / 相机、Stopped 状态、新文件路径与完成事件。
   不在 N.I.N.A. 中重复连接导星相机，不启动导星，不移动赤道仪，不改变 UVEX 光学机构。
3. SEP 做背景、源检测、连通区域和固定孔径测量。`focus_metrics.py` 用至少三颗星的平移共识
   配准，比较同一批孤立星的 R50/R80；不把星像圆度设为合格条件。
4. `FocusHost fit` **直接调用本机 N.I.N.A. 安装包的 `HyperbolicFitting`**，不是自行重写拟合公式。
   该命令只读测量点，不调用 N.I.N.A. AutoFocus VM，不会误用光谱相机作为星场输入。

源检测的既有父区域可能在离焦后被 SEP 分成多个子瓣：在已有孤立参考星和多星配准证据时，
对焦测量仍积分完整父区域，不拿某一个瓣当新星。未饱和固定孔径不使用碎小噪声标签作遮罩；
实际邻星、边缘、饱和、积分失败及信号不足仍不参与曲线。失败值是缺测，不是 HFR=0。
这条规则只用于对焦诊断，不授予目标身份或入缝运动权限，也没有修改生产目录识别门限。

## 本次实际数据（2026-09-13）

用户明确授权现场安全及设备移动。本次对象是 **Star Focuser Pro ASCOM / C11 主镜**，不是
测光电调焦，也不是 UVEX M2。原位5000；本机 N.I.N.A. 现有 Overshoot 参数为 In=100、Out=0，
未改参数。赤道仪继续原有恒星速跟踪，未转向、脉冲或重新标定。为拍摄打开了原本关闭的平场盖。

采集记录位于本机忽略目录 `output/sep-main-focus-20260913/`；所有 FITS 保留原路径和内容，
重新分析写入独立目录，没有覆盖原始在线判读。没有把真实帧、运行状态或本机配置加入 Git。

- 小步测试：5000 → 4950 → 5000，位置读回正确。
- 粗扫：4700–5300，100步间隔，每点3帧、3秒、增益100%。两颗跨全段可比较的星给出
  R50中位数：4700=10.41、4800=12.39、4900=7.43、5000=4.30、5100=4.76、5200=7.13、
  5300=9.99像素。5300只有1帧有效，未进入要求至少2帧的拟合。原生拟合低谷5030，
  R²=0.629；不把这条异常粗曲线直接用于自动落点。
- 首次3秒细扫参考星不足，被主动取消并返回5000；保留其9帧，不把零匹配当成焦点改善。
- 固定10秒细扫：先5000基准，再4900、4950、5000、5050、5100、5150，每点3帧。
  以下表格采用同一参考中的星1、2、3，均有完整的21帧跨焦位比较。

| 焦位（采样顺序） | 星群R50中位数 / px | 星群R80中位数 / px |
|---|---:|---:|
| 5000（基准） | 4.606 | 8.871 |
| 4900 | 4.477 | 7.928 |
| 4950 | 4.594 | 8.213 |
| 5000（扫回） | 4.334 | 7.769 |
| 5050 | 4.646 | 8.133 |
| 5100 | 5.809 | 9.339 |
| 5150 | 7.553 | 10.956 |

细扫调用的实际程序集是 `NINA.WPF.Base, Version=3.2.0.9001`，低谷4964，R²=0.935。
这是候选值而不是“4964精确最优”的证明：星场持续漂移、离轴像差、视宁度和镜面回差都会影响曲线。
不同轮次使用不同参考星，不能直接将粗扫与细扫的绝对R50拼成一条曲线。

随后安排5000 / 4975 / 5000 / 4975往返复拍，接受规则在采集前写入记录：至少三颗相同星、
每组至少两帧；4975在两次比较中均改善R50至少5%，且R80不恶化超过10%，才考虑改变原位。
否则保留5000，并报告已验证的较优区间，不声称获得显著改善。

实测验证结果：12帧均可比较同样的7颗星（本轮参考编号1–7）。

| 往返组 | 5000 R50 / px | 4975 R50 / px | R50改善 | 4975/5000 R80 |
|---|---:|---:|---:|---:|
| 第一次 | 5.241 | 4.942 | 5.71% | 0.935 |
| 第二次 | 4.963 | 4.882 | 1.62% | 1.005 |

第二次改善低于预先设定的5%，**因此最终保留5000**，不是“把4975/4964写成更优值”。
该5%只是本次候选落点的采纳标准，不是观测流程的新阻断门限。
现场共保留70张新原始导星帧，覆盖基准、方向测试、粗扫、主动取消的细扫、10秒细扫和验证。
`validation/decision.json`保存同星群、每帧测量和采纳判定。

检查：FocusHost 单独构建成功（0警告/0错误）；本次新增对焦测量/调试守卫测试17个通过；
整个 `scripts/star-detection` 测试集28个通过（包含真实 C#→Python 传输测试）；
冻结设计4份校验通过。没有修改 N.I.N.A./PHD2 Profile，没有安装插件或重启设备软件。

最终设备读回：Star Focuser=5000、停止；PHD2=Stopped；平场盖恢复Closed、灯关闭；
屋顶仍ShutterOpen，赤道仪仍恒星速跟踪、未转向。**这不是关台收口**：屋顶和跟踪保持任务开始状态。
`final-device-state.json`保留最终读回。

## 可复现入口

依赖使用独立 SEP 环境，不修改 `reduction/.venv`。构建只编译工具，不连接设备：

```powershell
.\.dotnet\dotnet.exe build src/UvexAdv.StarDetection.FocusHost -c Release
.\output\star-detection-venv\Scripts\python.exe -m pytest scripts/star-detection/test_focus_metrics.py scripts/star-detection/test_focus_commissioning.py -q
```

实机命令必须在重新核对本机 N.I.N.A. Profile、回差及行程之后明确运行。当前部件脚本是
Star Focuser 专用调试边界：目标4600–5500、距本轮原位不超过400步、单段最多150步；
最低位置包含本次已检查的100步向内回差余量。**这些不是所有开源用户通用的安全行程**。
换机器/换回差配置必须重新设定并验收边界，不能直接照抄。

```powershell
# 显式实机命令；Profile编号/相机名用现场读回值，不猜测。
$phdProfile = 0 # 替换为现场读回编号
$phdCamera = 'REPLACE_WITH_CONFIRMED_GUIDE_CAMERA'
.\output\star-detection-venv\Scripts\python.exe scripts/star-detection/commission_focus.py `
  --confirm-hardware --phd-profile $phdProfile --phd-camera $phdCamera `
  --positions 5000 4950 5000 5050 5100 --frames 3 --exposure-ms 10000 `
  --output output/new-explicitly-authorized-focus-run
```

默认所有扫描在分析前返回起点；`--leave-position`只能用于已经单独评估的验证目标，不能把
未经审阅的拟合最小值直接交给它。输出目录中创建 `STOP` 会在当前帧完成后的动作边界取消并尝试返回。
焦位遭到外部操作改变、设备状态不明、回程不安全时不强制覆写操作员状态；清楚记录未确认返回。
屋顶/平场盖由现场另行处理，扫焦脚本只检查状态，不自动开闭。

只读重放和原生拟合：

```powershell
.\output\star-detection-venv\Scripts\python.exe scripts/star-detection/replay_focus.py `
  --input output/run --reference output/run/00-focus-5000-f0.fit `
  --output output/new-focus-replay --ids 0 1 2 `
  --dotnet .dotnet/dotnet.exe `
  --fit-host src/UvexAdv.StarDetection.FocusHost/bin/Release/net8.0-windows/UvexAdv.StarDetection.FocusHost.dll `
  --nina-install "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy"
```

上述部件级调试时没有新增/安装主控台对焦按钮；后续 `.191` 新增入口见本文顶部。
没有改变 N.I.N.A. 当前默认星点检测器。完整的
PHD2帧→主 N.I.N.A.原生AutoFocus窗口适配、随观测序列自动对焦、狭缝方向包围能量或实际
光谱通量最优验证，仍是后续独立工作。尤其不能把本次对焦位置当成新的入缝证明；下一轮
观测必须重新取得本轮目标/狭缝证据。
