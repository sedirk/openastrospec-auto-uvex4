# 2026-10-09：全姿态无干涉屋顶模式与十目标序列启动

## 授权与范围

台站所有者明确确认屋顶与赤道仪在所有姿态下均无机械干涉，并授权修改本台站配置与
对应设计、验证、安装以及继续今晚原生十目标序列。新增 [ADR-0021](../adr/0021-station-attested-noninterfering-roof.md)
只替代该台站开关顶的 Park 前置条件；其他台站默认仍为 Park 联锁。

本台站不在开顶前发 Park，仍要求新鲜 Safe/天气/地平线、设备身份、支架停止、跟踪关闭、
无导星脉冲及 PHD2 停止。结束或 Unsafe 收口先停止采集与运动、确认关顶，再独立、有界地
尝试 Park。失败的 Park 不能阻止先前关顶，也不能被写成成功；RRCI 底层联锁不被绕过。
普通取消仍不主动触发机械收口。

## 配置与安装

- 插件：`0.4.0.242`。
- DLL SHA-256：`F327CB77BECAC564497889B59C8A6FFB938D6365BE6BED38235E2A8989311B6C`。
- 新 commissioning preset：`DF-UVEX4-FIELD-20261009-AIWEATHER-NONINTERFERING-ROOF`。
- Preset SHA-256：`034418A669C19FCE5004F0B96D7851F367209B966A6424A226DE3FF233836A84`。
- 台站确认绑定 `ASCOM.OnStep.Telescope` 与 `RRCIAdvanced.Dome`，不是通用跳过开关。
- 原 Night Setup、光学标定日期、硬件指纹与原始观测文件未改写；新包独立保存于机器本地。
- Profile 只更新六项方案选择/散列/屋顶模式字段，其他设置保持不变。

正常取消旧运行，确认采集及运动空闲后备份 Profile、旧插件和今晚序列，19:49 正常退出
N.I.N.A.，没有强杀设备所有者。安装的 41 个文件均与发布产物散列相同。19:50:12 重启
N.I.N.A. 3.2.0.9001（PID 2192），已核对实际加载版本与 DLL 哈希。PHD2 和后台设备服务
没有重启。备份在忽略目录 `output/night-20261009/preflight-backup/before-242/`。

实际自动观测面板、ATR 单帧检查展开区与校准库均打开核对；没有启动校准库或额外测试曝光。
本次启动日志未检出插件 XAML/Binding/dispatcher 未处理异常。ATR 首次连接仍有已有驱动
`Could not set TemperatureSetPoint to 1000` 记录，随后已请求正常 −10°C 制冷，不能称整份日志零错误。
19:59:58 读回温度 −10.1°C、目标 −10°C、制冷开启；没有因该初次日志进行重复重连。

## 验证

- 冻结设计四文件 SHA-256 校验通过；按本次明确设计授权更新基线及哈希。
- 完整构建与 **2,668 项 .NET 测试**通过。
- 后期处理 Ruff 与 **75 项 pytest**通过，未升级科学依赖。
- 新 commissioning 包正式校验 `Valid=true`、无问题项。
- **130 个 UI 场景**渲染；124 个与已检查的前版相同，6 个变化/新增场景逐一检查。
- 回归覆盖默认 Park 模式、确认缺失/身份不符/时效错误、配置锁定与漂移、停止状态、
  stop → close → park 顺序、Park 失败/超时、取消及原生序列共用入口。

软件验证日志与渲染在忽略目录 `output/night-20261009/release-242-*`，不纳入 Git。

## 本晚启动的实际证据（北京时间）

重新载入原始十目标序列，通过 N.I.N.A. 正常验证启动，没有使用 `skipValidation=true`。
起始 19:00 已过，等待时间自然为零；AIWeather 已连接且 Safe。

| 时间 | 已确认事件 |
|---|---|
| 19:56:22 | 原生整夜容器与首目标 WR 134 开始 |
| 19:56:35.5499 | 通过已绑定 RRCI/N.I.N.A. 请求开顶，原状态 Closed；没有请求 Park |
| 19:56:50.8067 | N.I.N.A. 回读 `ShutterOpen` |
| 19:56–19:57 | 开顶后只读检查：`AtPark=false`、无 Slew/导星脉冲、跟踪关闭；AIWeather Safe |
| 19:56:52.9796 | 原流程请求打开镜盖 |
| 19:57:30 | Night Setup 通过，进入目录目标转向阶段 |
| 19:57:32.1560 | 原生主控开始向 WR 134 转向 |
| 19:58:39 | 目录转向完成 |
| 19:58:44 | QHY 广域解算与广域见证通过，进入 G3 狭缝相机取场 |
| 19:59:58 | 仍为 `RunningAuto`，G3 取场阶段，没有失败代码；ATR 尚未开始光谱曝光 |

运行 `UVEX-20261009T115622Z-c68d51db60704f6` 的 manifest 保存
`roofClearanceMode=NonInterfering` 与上述新 preset hash。
整夜 journal 为 `night-20261009T115622958Z-0208400d9d254cbe9a6ef2f47c2fcbaf.json`。
这些是机器本地运行证据，不加入源码仓库。

**当前验收边界：已证明真实原生序列在未 Park 时开顶到位并越过 Night Setup。**
Night Setup 的完整结果为 `REAL_NIGHT_SETUP_LOCKED_WITH_OPTIONAL_DEGRADATION`（通过但有警告），
没有把它改写为无警告通过。
这不等于十目标均成功、真实跨中天续拍成功，或本版本已经完成整夜/Unsafe 关顶及 Park 故障现场验收。
后续各目标成功、质量跳过、设备/天气停止与最终关顶/停放结果须分别以新的 journal 和 manifest 为准。

## 20:02 失败复查与 0.4.0.243 修复

上述 .242 原生序列的首目标 WR 134 因 `G3_CATALOG_SHORT_POSITION_UNCONFIRMED` 停止；
其采集停止及运动结清检查均为空错误，但未进入下一目标。不是没有用原生序列，也不是
把目标中心判定放宽即可解决。后续九目标原生外层显示 FINISHED，其内部阶段仍 CREATED，
不能据此外层状态声称这些目标已经观测。原始记录保留不动。

代码复查和原生序列回归定位到三个生命周期问题与一个接口语义问题：

1. 终态质量门通知先释放 N.I.N.A. 阶段项，协调器仍在完成通知/持久化，旧逻辑误将这段
   间隙当作原生条件结束本夜。现以明确终态门标志区分，仍等待协调器结束后决定跳过。
2. 下一目标初始化时可能短暂收到上一目标的 Faulted 快照。现在按 run ID 隔离监听，
   不让旧失败取消新桥接器。新回归先暴露此问题，修复后首目标跳过、第二目标完成。
3. 完整安全链下，单目标失败清理遗漏整夜延后收口设置，过早关盖/尝试关顶。现在失败门、
   重新验证和统一清理均服从夜间所有权；夜间容器在最终结束或 Unsafe 时统一机械收口。
4. N.I.N.A. 3.2 的 `SetTrackingEnabled(false)` 返回实际跟踪状态，false 是关闭成功的正常值。
   旧代码误报 `Tracking-off command rejected`。现在不以该返回值判断成功，而以有界的连续
   fresh readback 检查身份、连接、Slew、pulse guiding 及 Tracking 均正确。

保留目标识别、AIWeather、设备身份、运动账本及停止检查。没有扩大质量失败允许列表，
没有把未知异常或停止未确认放行；内部执行/持久化异常会明确阻断跳过。

专项 **68 项**、全量 **2,679 项 .NET**、后处理 **75 项 pytest** 与 Ruff 通过，
编译 0 警告/错误，冻结设计校验通过。本次没有改动冻结设计或台站配置。
**130 个生产 UI 场景**重新渲染，与 .242 已检查图逐字节相同。
版本 `.243` 的 DLL SHA-256：
`5FF32992D9BFCA70C2981892521557F8C95AED97D9E56EA43CF003BC5CE5D81F`。

20:25 原生停止入口确认序列停止；旧协调器已经 Faulted，取消按钮按设计禁用。
随后再次确认 ATR 无曝光、PHD2 Stopped、赤道仪无 Slew/pulse/tracking、调焦器无运动。
备份位于 `output/night-20261009/preflight-backup/before-243/`。20:26:59 正常退出 N.I.N.A.，
未强杀、未重启 PHD2 或后台设备服务。

### .243 安装与原生序列重新启动

41 个安装文件与最终发布产物逐一校验相同。20:28:29 重启 N.I.N.A. 3.2.0.9001，
PID 26496；启动日志与后台桥确认 .243 和上述 DLL 哈希。光谱 Profile 未切换，
实际自动观测、校准库及 ATR 完整单帧展开区通过 computer-use 检查；截至 20:29:39
启动/面板日志无 ERROR/FATAL、XAML/BindingExpression/未处理异常。该结论不包含随后
真实设备连接期间可能出现的驱动日志。PHD2 原 PID 23080 保持不变。

重新载入原十目标 JSON，十项均为真实模式和“质量失败确认停止后跳过”；前三项
2×300 秒、后七项 2×120 秒，每项目标最多四次科学尝试，保留 01:00+08:00 截止、
−12° 晨光停止及原生光谱中天翻转触发器。没有修改模板中的目标顺序、时间或曝光预算。
AIWeather 明确连接且 Safe 后，于 **20:32:20** 通过原生
`sequence/start?skipValidation=false` 单次启动，未调用 Dockable `start-selected`。
高级序列实际窗口确认等待时刻/等待安全均已完成，整夜目标 1/10 WR 134 正在执行。

本轮运行：`UVEX-20261009T123220Z-a2d29af7248144a`；整夜 journal：
`night-20261009T123220898Z-52aaf3ebdbcf48bd95814e5defd666fd.json`。
20:32:58 读回 AIWeather Safe、屋顶 Open、赤道仪静止且未 Park，镜盖正在开启。
此时尚未开始光谱曝光，不能把原生启动回执称为科学完成或整夜收口验收。

### .243 实机后续与 .244 分类补充

20:37:04 WR 134 实际返回 `G3_POST_WCS_SHORT_UNCONFIRMED`，内部为持续分裂星像
`G3_SEP_PARENT_BLEND_UNRESOLVED`。原生 journal 的 `qualitySkipRequested=true`，
`endRequested/unsafeTripped/operatorCancelled=false`，`boundaryFailure=null`，
`idleErrors/settledErrors=[]`，终态代码与质量门一致。前三处生命周期修复正常生效，
但这个后 WCS 短帧错误不在原来的五项允许列表中，本夜仍按设计记录 Stopped 而没有跳过。
原始记录保持不动；不能将 .243 的实机启动报告为已验证目标接续。

.244 仅将后 WCS 短帧未确认和交接几何窗口外两类终态图像失败纳入明确质量分类，
保留停止、身份、安全、账本、取消和精确终态匹配等所有独立检查。增加原生双目标
执行回归，覆盖这两类失败的继续、显式结束策略及清理失败不继续，保留未知异常阻断测试。
不放宽星像识别门，不对本轮失败图像声称入缝或成功曝光。

本轮收口实际于 20:37:44.3247 请求关顶，20:38:00.9101 回读 `ShutterClosed`；
随后独立请求 Park，没有再出现 tracking-off 返回值误报。20:40 采集空闲、导星停止、
赤道仪无 Slew/pulse/tracking，但 `AtPark=false`。为安装修复于 20:40:15 正常停止原生序列，
journal 保留 `Roof closed; N.I.N.A. did not complete independent Park.`，不声称全套收口成功。
镜盖和屋顶已关闭。备份 `before-244` 后，20:42:02 正常退出并断开设备；未强杀或重启 PHD2。

.244 原生序列专项 33 项通过。首次全量测试出现一次独立 QHY 模拟测试的临时 manifest
读写共享冲突（`CumulativeAcceptedCountAndFrameIndexRemainCompleteBeyondRecentFrameWindow`）；
原失败日志保留，QHY 46 项单独复跑通过。没有为绕过该测试而改动 QHY 代码或依赖。

第二次完整 `scripts/build.ps1` 全部 **2,686 项 .NET 测试**通过，编译 0 警告/错误；
Ruff、75 项 pytest、四文件冻结 SHA 校验通过。130 个生产 UI 场景重新渲染，与前次
逐字节相同。最终产物 41 个文件安装后逐一校验一致；插件 `.244` DLL SHA-256 为
`7CBDA80EA4D1F20D298ED5C875ED48F53C56CE50C5123590F7FEA3C1BCCB34C3`。

20:44:12 正常启动 N.I.N.A. 3.2.0.9001 / PID 19040，后台桥加载版本和 SHA 与产物一致。
实际打开自动观测、校准库、ATR 展开的单帧检查区，未执行检查曝光；启动及上述面板日志
截至 20:46 无 ERROR/FATAL/XAML/Binding/未处理异常。日志为
`20261009-204412-3.2.0.9001.19040-202610.log`。没有修改设备所有者或重启 PHD2。

重新载入同一十目标原生高级序列；目标顺序、每目标帧数、曝光、失败策略、晨光与截止
均不变。**20:47:53** 回读已连接的 `AIWeatherSafetyMonitor` 为 Safe，使用原生
`sequence/start?skipValidation=false` 单次启动；真实高级序列窗口确认时间/安全等待完成，
WR 134 的 Night Setup 开始。未调用面板独立启动或绕过原生验证。

### .244 跟踪状态误判与 .245 补充修复

.244 于 20:48:21 由序列确认开顶，20:48:59 在 GOTO 前停止，代码为
`TELESCOPE_TRACKING_ENABLE_REJECTED`；没有进入 G3 或 ATR 曝光。不是又一次
图像质量跳过失败：该设备级代码不在允许列表，整夜停止正确。
20:49:55 屋顶实际回读 `ShutterClosed`，随后独立 Park 仍未确认，20:52:55 journal
保留 `Roof closed; N.I.N.A. did not complete independent Park.`。不能声称十目标已运行成功。

本轮 `telescope-tracking-enable` 原始审计明确记录 `accepted=false`、`verified=false`、
`pollCount=0`、耗时 **103 ms**，虽然配置等待为 15 秒。核对当前 N.I.N.A. 3.2 源码，
`TelescopeVM.SetTrackingEnabled` 返回 setter 后的瞬时 Tracking 值，而非命令接受回执。
前序只修正了关闭跟踪的相同接口误用，开启路径仍以 false 直接拒绝并跳过等待。
因此本次日志不能证明 OnStep 明确拒绝命令，也不能凭这条记录决定应当 Home。

.245 对每次开启检查只发一个跟踪命令，无论瞬时返回 true/false，均根据后续读回决定结果，
仍保留每次原 15 秒等待；使用单调时钟，轮询中不重发命令、不自动 Home、不忽略真实超时。
既有 `RetrySameStage` 策略最多重试两次，未在这次变更中扩大或重置。
等待期间连接、精确设备身份、Park 及外来 Slew/pulse 均单独检查，任何异常不授权 GOTO。
只有实际接口异常才标记 REJECTED，持续跟踪关闭标记 TIMEOUT，审计保存瞬时值、
命令次数、轮询次数、实际等待和最终 gate，避免再把状态值称为 accepted。
新增模拟真实 mediator 返回语义的测试，不访问设备或写入生产观测审计。

.245 专项 51 项（含 17 项新的跟踪接口回归），全量 2,703 项 .NET、75 项 pytest、
Ruff、冻结校验通过，编译 0 警告/错误。130 个离线 UI 场景与 .244 逐字节一致。
原生停止后确认相机/导星/主动运动空闲，备份 `before-245`，21:04:35 正常退出；
41 个安装文件与最终产物相同。DLL SHA-256：
`4D6030DDE10CC94BC89CCFDA6D85AFAE2E7F37AE1E669B37CCE710AFEDA8F44E`。
21:05:10 重启 N.I.N.A. / PID 25860，版本及 SHA 回读一致。实际自动观测、校准库、
ATR 单帧展开区检查通过；截至 21:07:41 启动/面板日志无 ERROR/FATAL/XAML/Binding/
未处理异常。后续设备连接日志仍有 ATR 驱动尝试设置 1000 的历史模式错误，
不能把启动检查推广为全部设备日志无错误。PHD2 与设备服务未重启。

同一十目标 JSON 在 AIWeather 连接并明确 Safe 后于 **21:07:57** 单次从原生
`sequence/start?skipValidation=false` 启动；运行
`UVEX-20261009T130757Z-e48b7ab39653472` 已进入 Night Setup。没有独立启动单目标。

这次实际复测在 21:09:49 返回 **`TELESCOPE_TRACKING_ENABLE_TIMEOUT`**：
`commandCount=1`、`pollCount=60`、`elapsedMilliseconds=15000.7178`、瞬时和最终跟踪值
均为 0，连接正常、AtPark/Slew/pulse 均为 0。这是最后一次检查；完整记录含原始检查及
既有策略两次重试，共三个单次请求、三段各 15 秒等待，均超时，不能把最后一条审计的
`commandCount=1` 当作全轮命令总数。因此已经证实旧的提前退出已修复，
但 **未证实赤道仪能开始跟踪**，不能将软件回归称为真实观测恢复。
首目标未进入 GOTO/G3/ATR；其余九目标没有开始。本次设备级失败仍结束整夜，
没有作为“单目标质量失败”跳过。新 night journal 为
`night-20261009T130757741Z-182d215736bf4281a89518d5db316deb.json`。

21:10:45 实际确认屋顶 `ShutterClosed`，镜盖 Closed；ATR 空闲、PHD2 Stopped，
赤道仪无 Slew/pulse/tracking。随后原生容器独立等待有界 Park。此时 AtPark/AtHome
均为 false，报告高度约 −0.02°；这些读回不足以确认限位原因或授权 Home。
没有重复启动序列、强制回零或修改限位/固件；继续观测需要先恢复并验证赤道仪状态。

21:13:45 有界 Park 结束，整夜 journal 进入 Terminal，仍如实记录屋顶关闭而独立 Park
未完成。21:14:10 再读回镜盖/屋顶 Closed、ATR 未曝光、PHD2 Stopped、赤道仪
Tracking/Slew/pulse 均 false、AtPark/AtHome 均 false。本轮收口已结束，没有遗留正在等待的
原生 Park 动作；停放姿态本身未取得确认。已请求用户另行授权一次有界 Home 后核验再续跑，
本次交付未执行 Home，也未把十目标序列标记为成功或持续运行。

## 0.4.0.246：显式整夜 Home → Park 与单次维护结果

所有者随后明确授权：通过 N.I.N.A. 执行一次有超时限制的 Home，核验后再启动同一
高级序列，并把自动 Home/Park 加到整夜收口。此授权不改变已失败旧轮的结果，
不允许无界重试、改限位/固件或伪造到位。

21:39 的新鲜读回：ATR 空闲，PHD2 Stopped，屋顶/镜盖 Closed；赤道仪连接身份正确，
无 Slew/pulse/tracking，AtHome/AtPark 均 false。旧 native night 已 Terminal，
目标 idle/settled 错误均为空。经原生 sequence/stop 确认退出，维护脚本针对已终结的
Faulted 目标要求匹配的 night journal、空闲 native sequence 和当前真实设备检查，
不把 Faulted 改为成功、不清账本。

21:41:40.9321 N.I.N.A. 记录一次 FindHome；21:41:44.4645 原生日志即记录
“Mount has located its home position”，但后续 180 秒 N.I.N.A. API 的 AtHome 始终
为 false。21:44:41 保存 unconfirmed-home，并通过 N.I.N.A. slew/stop 请求停止。
没有第二次 Home、Park、曝光或序列启动。审计目录：
`%LocalAppData%/UVEX-ADV/maintenance/nina-home-20261009T134140Z-006bcd7b/`。
这只证明原生完成信号与实际状态不一致；没有足够证据确认控制器拒绝原因或真实姿态。
21:49 再次确认镜盖/屋顶关闭，采集及运动空闲；原生 Home 任务早已返回，没有遗留等待。

新的序列选项 `HomeBeforeParkOnFinalize` 默认 false，Home 超时默认 180 秒、允许
30–600 秒。今晚两个既有序列副本显式设 true，目标、曝光、截止和失败策略保持不变。
保存/复制/重载保留该选项，启动时锁定并纳入每轮动作哈希，运行中的编辑不改变它。
真实启用要求已确认全姿态无干涉且完整安全链；执行顺序是停止采集/导星、关盖、
停止赤道仪、关顶核验、Home、Park。普通取消无机械收口，已 Park 不为 Home 解除停放。
Home 与 Park 各自限时且各至多一次；Home 必须原生任务完成并有两次停止的 AtHome
读回，失败请求停止、不再 Park。失败不会重开屋顶，也不清理未结运动责任。

回归覆盖 native plan 默认/验证/复制/JSON、Home 的成功/仅 ack/错误读回/不返回/
超时/取消/身份变更/错误状态，以及共同屋顶收口顺序。维护脚本改为单调计时、
超时请求停止；坐标命令扫描精确排除无坐标的 `/slew/stop`，仍约束所有坐标 slew
和未知后缀，并新增离线断言。第一次全量检查捕获旧脚本计时断言与新增英文文案
遗漏，已补齐；不把这次失败隐藏为首轮全绿。

.246 全量 2,721 项 .NET、75 项 pytest、Ruff 及冻结设计校验通过，编译零警告/错误。
130 个离线 UI 场景完成，128 个与 .245 逐字节一致，两个整夜编辑场景已检查。
21:49:35 正常退出后备份 Profile/插件，安装的 41 个文件与产物相同；21:52:39 重启
PID 11056，回读版本与 SHA-256 一致。原生高级序列已重新载入，Home 开关 true、180 秒，
十个目标均 CREATED，没有调用 sequence/start，也没有重新连接设备。

### .247：实际 N.I.N.A. 开关标签与最终交付

实际高级序列检查发现：N.I.N.A. 的 StandardCheckBox 模板不包含 ContentPresenter，
导致 CheckBox.Content 中的“收口先 Home 再 Park”与既有“晨光结束”标签都只剩 ON/OFF。
改为独立 TextBlock 并使用 AutomationProperties.LabeledBy 关联，保留原绑定与取值。
离线整夜场景改用安装版 N.I.N.A. 的真实 CheckBox 模板；首次运行暴露离线主题缺少两个
Brush，补齐测试主题后重跑，不改生产主题、不弱化断言。

最终 .247 全量 **2,721 项 .NET、75 项 pytest、Ruff、冻结设计校验**通过；编译零警告/
错误。130 个界面场景全部生成，128 个与 .246 一致，仅两个整夜场景变化，宽/窄均已查看。
22:02:37–38 在所有 N.I.N.A. 设备未连接、无运行的边界正常退出，保存 `before-247`
Profile、插件及序列备份。41 个安装文件与最终产物一致，DLL SHA-256 为：
`9711C00F51D874CBAEE0B2334F6945C3C67A386ABEBA944FCE089CB095A11082`。

22:04:43 正常重启 N.I.N.A. / PID 21976，桥回读 .247 和上述 SHA 一致。实际查看自动观测、
校准库、ATR 展开检查区及原生高级序列，Home/Park 标签及 180 秒读回清晰可见，未采集。
截至 22:06 启动/面板日志没有 ERROR/FATAL/XAML/Binding/未处理异常：
`20261009-220443-3.2.0.9001.21976-202610.log`。PHD2 PID 23080 未重启。

同一十目标序列载入成功，目标全部 CREATED、UseRealMode=true、原失败策略不变，
截止仍为 2026-10-10 01:00 +08:00；HomeBeforeParkOnFinalize=true、超时 180 秒。
**序列没有启动**：单次维护 Home 未取得 AtHome，到位条件不满足。设备未重新连接，
没有第二次 Home/新 Park/开顶/曝光。最后一次连接状态检查已确认屋顶/镜盖关闭、
采集及运动空闲；重新启动后的未连接状态不冒充新的硬件读回。仍待所有者提供
21:41 Home 的实际运动/姿态观察，才能区分未执行运动与到位读回异常；本交付不宣称
Home/Park 实机验收或十目标观测成功。

### 所有者确认机械零位后的续跑与 .248 修正

所有者现场确认已到机械零位并要求继续。保留原来 `AtHome=false` 的审计，不伪造到位，
本轮没有再发 Home。22:17:31 通过主 N.I.N.A. 重连原 OnStep 时，COM6 返回访问拒绝；
只读设备/句柄检查后，一次有界重连在 22:19:41 成功，身份匹配、无 Slew/pulse、
Tracking=true。未改串口或限位，未强停进程；瞬时占用来源未取得证据，不推定为 PHD2。
AIWeather 22:17:46、22:20:37 新分析均 Safe，PHD2 只读状态 Stopped。

22:20:32 原生 sequence/start 的正常检查发现重启后的 Profile 为模拟模式，取消该次
启动，使用可见“真实”控件同源命令切换后，于 22:22:00 再走原生高级序列。
并非单目标面板启动，也未使用 skipValidation。首目标 WR 134 在计划预检查即失败：
22:54 的预计高度 41.75° 小于原 42° 限制，尚未连接 ATR、开顶、GOTO 或曝光。
旧实现随后要求尚未连接的 ATR 提供空闲读回，因而停止整夜；其余目标没有观测，
原生容器的 FINISHED 不代表科学完成。原始 run
`UVEX-20261009T142200Z-f067c206ba3041b` 和对应 night journal 均保留。

.248 在目标 runner 创建之前加入独立高度调度拒绝：仅有效计划、明确跳过策略且本夜
仍允许继续时记录 `SkippedPreflightHorizon`。它保留原高度/时长，既不重置运动责任，
也不把运行中设备或安全失败加入可跳过名单；下一实际目标仍执行完整 Night Setup。

专项 39 项通过，完整发布检查 2,726 项 .NET、75 项 pytest、Ruff 与四文件冻结设计校验
通过，编译零警告/错误。新增用例覆盖首目标/中间目标高度跳过时不创建 runner、后续
原生目标继续、无效计划及 EndNight 策略不降级。22:28 读取屋顶/镜盖 Closed，赤道仪
Tracking/Slew/pulse=false，ATR 从未连接或启动曝光，PHD2 Stopped；原生 stop 后正常
退出，备份 `before-248` 的 Profile、插件和原序列。41 个安装文件与产物逐一核验。

22:29:59 正常重启 PID 22200；桥回读 .248，插件 SHA-256：
`48676D5C3B14328C37434BAC9A50895FDB2121A8FF90E5BD4FA81368A32FA31A`。
原生高级序列实际界面已核对 Home/Park 开关、180 秒及原截止。重连原 mount/AIWeather
并重新选择真实模式，22:30:42 新天气分析 Safe；随后经正常校验再次启动同一十目标序列。

22:31:07 原生整夜序列开始，WR 134、WR 136、WR 137 分别记为
`SkippedPreflightHorizon`，未创建真实目标 runner、未计为观测成功。随后第 4 项
V460 Cyg 建立运行 `UVEX-20261009T143107Z-5fe780db6d60439`；屋顶 22:31:35
读回 ShutterOpen，镜盖随后 Open，22:32:13 进入目录转向，赤道仪报告残差 1.2″。
此报告仅为赤道仪坐标读回，不冒充独立星场解算。整夜审计为
`night-20261009T143107463Z-23e886458c1b4567ad8ce3b642d5f5d5.json`。

22:33:21 广域相机的独立解算未成功，按既有可选广域回退路径记录
`QHY_WIDE_FIELD_DEGRADED_TO_G3`，未以失败的 QHY 解算结果发居中修正；转由新鲜
G3 WCS 负责取场。截至 22:36，原生 night 与 V460 Cyg 均 RUNNING，运行状态
RunningAuto/AcquireG3SlitField，G3 新 FITS 持续落盘。AIWeather Safe，屋顶 Open、
镜盖 Open/照明关闭，赤道仪 Tracking=true、Slewing/pulse=false。ATR 科学曝光计数
仍为 0；这是序列启动和前置跳过的实机验证，不是入缝、光谱采集或最终 Home/Park
成功的验收。原始失败轮和新轮数据均未修改。

## 22:49 重试与 .249 开顶准备修复

22:49:32 用户确实通过原生高级序列再次启动，并非单目标面板入口。实际 run
`UVEX-20261009T144932Z-d3cbcd84cc6c469` 在 22:49:35 的 Night Setup 报
`ROOF_OPEN_MOUNT_MOVING`。无干涉模式要求无 Slew、无导星脉冲、跟踪关闭，但旧实现
只检查组合状态，没有先关闭残留跟踪；开顶命令根本没有发出。旧错误未保存三项状态
数值，因此不能把当时具体哪一项未通过、风或驱动故障推定为已证实；AIWeather 当时 Safe。

该轮随后在 22:49:43 取得原生 Home 完成记录、22:49:47 取得 Park 完成记录。
对应 night `night-20261009T144932556Z-0bb063481b134673b669ab4ec3f58519`
已 Terminal，停止/结清与收口错误列表均为空；后续 22:51、23:06 的新鲜读回均为
AtPark=true、无 Slew/pulse/tracking、屋顶和镜盖 Closed。此结论取代上一轮停放未确认，
但不代表任何目标成功曝光。22:49:48 再次播放时所有原生项目仍是 FINISHED，启动与
结束仅相隔约 1 ms，没有创建新一夜或重新执行开顶。

.249 在两个前端共享的真实 runner 中增加开顶前有界准备：完整安全链、锁定设备、
空闲采集/PHD2 前提通过后，只关闭一次残留跟踪，10 秒内等待两次停止读回；不会在
开顶准备中 StopSlew、Home、Park 或重新导星。读回失败、身份变化或活动运动仍阻断。
RPC 导星复核之后重新检查同步安全/屋顶/赤道仪状态，再发开顶；取消不授权下一动作。
新增 `unattended-roof-open-preparation` 证据及具体中文失败提示。原生已结束项目仍需
人工重置执行状态或重新载入同一文件，不隐式重跑，不清除旧运动责任。

发布回归完成 2,747 项 .NET 测试（零失败、编译零警告/错误）、75 项 pytest、Ruff
与四文件冻结设计校验。新增回归覆盖跟踪设置返回值与真实读回的区别、只请求一次、
连续停止确认、超时、取消、设备变更及中英文错误呈现。没有放宽 AIWeather、设备身份
或停止条件。本次维护不启动真实序列、开顶或天空曝光；实际开顶仍待下一次正式启动验证。

133 个生产 UI 场景完成渲染、总览检查，新开顶失败的中英文/窄窗另逐张复核；旧 130
张与 .247 发布基线 SHA-256 完全相同。23:10 通过原生 sequence/stop 确认退出运行，
备份 `output/night-20261009/preflight-backup/before-249/` 的 Profile、插件及原计划后正常
退出 N.I.N.A.；未强停或重启 PHD2/设备服务。41 个安装文件与 .249 产物逐一一致。

23:11:02 重启 N.I.N.A. 3.2.0.9001（PID 21024），桥与加载日志均回读 .249，插件 SHA-256：
`29D3781502C2C35E4D15EB2383A646CBB46AC8C5DB48387111756EFD1C4C1195`。
自动观测、校准库和展开的 ATR 单帧检查均已实窗核验；截至 23:14 新日志无 ERROR、
XAML/Binding 或 dispatcher 异常。未打开物理相机，未曝光、Home/Park、转向或开关顶。

维护中所有者另要求十目标计划改为 23:30 开始。只修改等待时刻和两个显示标题，并同步
用户文件与本地 output 副本；十目标子树与安装前备份逐项序列化比较完全相同，仍保留
次日 01:00 截止、原曝光/高度策略和 180 秒 Home→Park 收口。原生 API 重载后，等待项
为 23:30:00，整夜及全部十目标均 CREATED，非上一轮 FINISHED；真实模式已恢复，
运行保持 Idle，未点击播放，后台真实动作授权未开启。90 分钟窗口不保证完成全部十目标。

23:15 最终读回：新版进程正常、日志异常数为 0；原生“等待到安全”显示“安全监视器尚未
连接”。这是正常重启后的未连接状态，不是新的天气 Unsafe 结论。启动前仍须由操作者
连接原 AIWeather 安全监视器并核实 Safe，再从高级序列播放；本次没有隐式连接或排队
启动真实设备。23:30 两份计划 SHA-256 相同：
`A5C7D6B112B80B5A8BD6CF8D51448E769B63D366A3D4DF06BE514FBAB94DDA9A`。
