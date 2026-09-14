# SEP 主镜对焦：程序入口与 Star Focuser 实机调试

最新晚间修正与在线验证见[2026-09-14 SEP 对焦恢复](sep-main-focus-recovery-20260914.md)：
基准组不再进入扫焦拟合，共同星群按每焦位两张完整帧选取，原位验证与显著改善分开记录。
.199 的最后一轮实际达到 `VerifiedAtOrigin`、`FocusVerified=true`、`ReturnConfirmed=true`，
停于原焦位 5000；未宣称候选焦位优于原位。后续 .203 已安装并核验原生面板，
用户结束本夜测试后正常退出 N.I.N.A.，完整状态见[9 月 15 日收口](closeout-2026-09-15.md)。

后续记录：2026-09-14用户对焦失败后的[Star Focuser回差实测](star-focuser-backlash-20260914.md)
取得约180–200步量级估计，并发现`.192`把扫焦前基准组混入拟合的问题。回差重复扫描未完成，
没有将那次诊断当成生产对焦成功；当时待完成的补偿及焦点复核由上述 .199 记录接续。
以下保留各次安装和验证的历史边界。

## 对焦入口与原生窗口修订（0.4.0.192，2026-09-14）

本节是当前实现；后面的 `.191` 段落保留为当时的历史交付记录。
`.191` 后续已经按用户请求安装；`.192` 于2026-09-14本地00:37按用户新授权安装并重启。
**精确产物和启动状态已核验，未曝光或移动设备；原生窗口完整检查与真实对焦验收仍待完成**。

### 为什么出现“点击没用”和“好像没有取消”

只读后台快照确认观测已经是 `Cancelled`，该轮 manifest 在本地23:39:51结束更新。
之后主镜对焦确实收到启动请求，但 Profile 中最小/最大焦位都是0，执行器拒绝无效范围。
旧页面只把原因放到长表单下方，运行与对焦状态也不够清楚；不能据此断言仍在曝光。
取消观测不等于关台、关闭屋顶或赤道仪回零，本文不把它们混为同一动作。

现在无效范围/曝光/数字格式在启动前显示于固定提示条，并禁用启动；程序化调用同一个
执行入口仍会再次校验，参数无效时连设备就绪读回也不发生。观测处于取消、收尾、暂停或
运行状态时，提示它仍占用设备；暂停不当作取消。终态后的排队进度不会再把运行条刷回
“正在解算/曝光”。对焦取消只有实际执行结束后才显示“已取消”；未确认回位继续明示。

### 新位置和参数含义

- 插件入口：`设备手控 → 主镜对焦`。原UVEX4控制在相邻的`光谱仪 · 狭缝 / M2`子页，
  不改变其服务所有权；`自动准备`仅保留“主镜对焦…”导航按钮。
- 对焦内部是`设置 / 曲线 / 记录`，启动条件、执行状态、开始/取消按钮固定在上方。
  内容区可滚动，不让小窗口裁掉底部设置；曝光单位改为秒，持久化仍为毫秒。
- 最小/最大焦位是实际允许范围，不是扫描目的地。采样在读回的当前焦位两侧，
  并在移动前核对整个范围及当前 N.I.N.A. 回差补偿余量。不会以本机5000为通用默认。
- 采样时逐点更新曲线；R50与HFR同为半光半径，这里是SEP固定孔径、同星群的测量。
  原位—候选往返验证使用新的局部参考，不混入之前的扫焦曲线；缺测不填零。

### 确实复用原生 AutoFocus，而不切换相机

N.I.N.A. 3.2内置 `AutoFocusVM` 使用当前主相机的 `IImagingMediator.CaptureImage`。
本系统该相机是光谱相机，单独替换“星点检测”方法不会让它改从PHD2取图。
但 N.I.N.A. 提供 `IAutoFocusVMFactory` 扩展点，因此不是不能使用原生页面。

新增可选实现 **`OpenAstroSpec SEP · 主镜 / PHD2`**（英文界面为`Main mirror / PHD2`）。
在 N.I.N.A. `选项 → 拍摄（Imaging）→ 图像选项 → 自动对焦`实现选择器中选它，
再使用主控台原生“自动对焦”窗口；
这不是“星点检测”选择器。本次代码不自动替用户切换该设置。

| 入口/部件 | 实际复用与边界 |
|---|---|
| 原生自动对焦窗口 | N.I.N.A. `AutoFocusToolVM` 原有开始/取消生命周期、历史记录选择器 |
| 可选对焦实现 | `SepMainFocusFactory` 返回插件同一个 `SepMainFocusViewModel`，通过MEF标准导出，不使用反射替换私有字段 |
| 原生与插件曲线 | 都使用 N.I.N.A. 程序集中的 `NINA.View.AutoFocusChart`，不是手绘近似图；曲线页打开时才实例化 |
| 执行 | 原生 `StartAutoFocus`、插件按钮、后台 `start-main-focus` 共用 `ExecuteAsync → RunFocusPreparationAsync → SepMainFocusRunner` |
| 取消 | 原生传入的 CancellationToken 与插件“取消对焦”汇入同一执行取消源，等待同一有条件回位 |
| 主镜 | 当前主控N.I.N.A.电调焦及原生回差补偿，仍由 `IFocuserMediator.MoveFocuser` 执行 |
| 图像 | PHD2持有导星相机并采集新帧；不让主N.I.N.A.重复连接导星相机，不对光谱相机曝光 |
| 历史报告 | 只有往返验证证实改善且最终位置确认时生成原生报告；先写独立临时文件再原子发布JSON，避免历史监视器读取半文件 |

原生窗口的外层开始按钮仍要求 **N.I.N.A.主相机与电调焦已连接**，并由它对主相机登记采集锁。
这是N.I.N.A.窗口自身的门槛，不代表本实现拿光谱图对焦。插件独立入口不再错误地依赖
光谱相机的手动曝光按钮是否可用；两个入口仍执行同一设备就绪检查和科学判定。
本实现不会自动停止PHD2正在进行的导星、打开屋顶/镜盖或转向；必要的准备由操作员先完成。

Profile切换时保留同一个VM实例，因为原生窗口会按CLR类型复用实例；取消旧运行后重新加载
新Profile参数，不把旧结果写进新配置。两个可见设置编辑器各自记录输入错误，不能互相
清除另一个窗口中的错误输入。历史曲线不是新的实时位置读回。

仍未验收：安装后的原生AvalonDock按钮/模板、实际Star Focuser新入口、原生序列中的自动
对焦、无人值守，以及狭缝方向能量或实际光谱信号最优判定。保留原位/取消/失败不会伪装成
成功的新焦点报告；因此尤其不能在未实测前把本实现直接声明为高级序列自动对焦已兼容。
冻结设备所有权、入缝身份检查和运动限制未修改。

### 本次验证

- `scripts/build.ps1`成功，0编译警告/错误、1803个.NET测试通过。
- 新增参数前置校验、取消/终态显示策略、多窗口输入错误、Profile重载、原生扩展契约、
  扫焦/往返曲线隔离测试；UI测试实际加载原生控件并核对固定区边界、按钮状态和底部可达性。
- 46个离线界面场景渲染并检查，产物在忽略目录`output/ui-focus-192-final/`。
  所有截图都是模拟图表/状态，不是本次天空采样。
- reduction的ruff检查通过，66个pytest测试通过；未改其代码和科学环境。
- 冻结设计4文件校验通过；原始观测文件没有改动。
- artifact：`artifacts/nina-plugin/UvexAdv.Nina.Plugin.dll`，版本`0.4.0.192`；
  SHA-256：`D60082478873E5F390CBF1FE0C20D34434C817730678A1505ED18163DB74215C`。
  初次源码交付仅构建；后续安装记录如下。

### 后续安装与启动核验（2026-09-14 00:37）

用户明确要求“安装并重启nina”时，旧N.I.N.A.已退出。先备份`.191`插件、N.I.N.A.
Profiles和SEP运行时绑定，再安装上述SHA-256的`.192`；全部17个artifact文件逐一核对一致。
现有SEP运行时与源码一致，未重新创建Python环境，未更改用户设备或对焦配置。

N.I.N.A. `3.2.0.9001`新进程`38288`响应正常，插件加载日志确认`.192`；后台快照为
`Idle`、`mainFocusBusy=false`、本会话真实控制未授权。N.I.N.A.相机、电调焦和导星适配器
均未连接，相机未曝光、电调焦未移动。新日志未发现XAML、Binding或未处理异常。

Advanced API切换到主控台成功，但`show-main-focus`的有限次后台导航请求返回
`STALE_REVISION`，没有确认主镜对焦子页实际打开。此项不能标记为通过；这些导航请求自身
不启动对焦或观测。最终快照的对焦提示是“主镜电调焦必须为当前N.I.N.A.选定设备，已连接、
停止且关闭自动温补”，同时读回电调焦未连接，主镜对焦不忙。安装操作未发起该对焦请求。
未用computer use，未选择原生SEP实现，三面板与原生窗口完整检查
仍待完成。安装成功、启动检查成功不替代这些前端与天空验收。

本机备份及核验回执（忽略目录，不入Git）：
`output/deployment-backups/sep-focus-192-20260914-003704/restart-verified.json`。
本次只安装并启动N.I.N.A.；没有重启PHD2或服务，没有曝光、对焦、回零或屋顶动作。

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
