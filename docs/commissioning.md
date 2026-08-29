# OpenAstroSpec Auto — UVEX4 实机调试清单

ATR585M 已于 2026-08-16 使用官方工具升级到 FPGA 5.0，并完成 SDK 60、原版 N.I.N.A. 驱动和 ToupSky SDK 59 三路连续帧回归。版本、校验值、测试数据和回滚方式见 [ATR585M SDK / FPGA 实机调试记录](atr585m-sdk-firmware-commissioning-20260816.md)。相机固件升级不等于 UVEX 电机闭环已经验收；以下 M2、光栅和狭缝调试门槛仍然适用。

## 1. 上线前备份

1. 导出 DRIVER.UVEX4 当前设置并保存其通信日志。
2. 记录光栅、狭缝和 M2 的当前机械位置、方向、行程和回零行为。
3. 确认 Windows 设备管理器中 COM5 对应 `VID_1A86&PID_7523`。
4. 保留旧 Manager 的安装文件；不要卸载或覆盖 UVEX 控制器固件。ATR585M 相机固件按上方独立记录管理。

## 2. 服务只读验证

1. 关闭 DRIVER.UVEX4，确认任务管理器中不存在其进程。
2. 保持 `Simulator=true` 完成管理器和 API 测试。
3. 将 `%ProgramData%\UVEX-ADV\config.json` 的 `Simulator` 改为 `false`，但暂时不要获取控制租约或点击运动按钮。
4. 启动服务，核对固件版本、功能位、温度、光栅位置、M2 位置和狭缝位置。
5. 身份不匹配时服务必须拒绝打开端口；禁止通过修改代码跳过检查。

## 3. 有界运动标定

1. 在可观察机械机构的维护时段进行，每次只测试一个轴。
2. 先测试急停，再以最小步数验证正负方向。
3. 测量并写入光栅和 M2 的软件最小/最大位置、单次最大位移和实际回差。
4. 逐一选择四个狭缝，分别取得不可变 G3 短/长曝光 `OFF×3 → ON×3 → OFF×3` HDR 证据并建立暗孔径双边宽度指纹；强反光边允许过曝，物理宽度必须取黑色不反光区两边之间的距离，反光脊 FWHM 不得替代缝宽。确认编号、名称、人工物理宽度和光电二极管检测一致，并验证四个实测宽度与 `15 < 25 < 35 < 300 µm` 同序。不得从一个轮位按标称微米比例推算其余轮位。完整契约见[狭缝轮光学身份复核](slit-wheel-optical-identity.md)。
5. 连续执行 50 次有界操作，并检查 `%ProgramData%\UVEX-ADV\logs` 和 `uvex-adv.db`。

赤道仪有两套用途不同、不得共用数值的当前生产运动门，另保留一套旧 QHY 门供历史取证：

- `G3WcsCenteringLimits` 管 fresh G3 正式解算后的 N.I.N.A. 大步 WCS 修正，独立声明单步、原点半径、累计量、动作次数、耗时、稳定等待和失败回程；每一步都必须用移动后的 fresh G3 WCS 证明光学响应和到位。
- commissioning preset 的 `Motion` 管 G3 邻场搜索和目标入缝等精细修正。不得为了容纳大步 WCS 残差而放宽精细入缝门，也不得让 WCS 粗定位复用 G3 像素→赤道仪微调转换。
- `QhyCoarseCenteringLimits` schema 1 只描述 ADR-0008 之前的 GS350/QHY 运动路线。新生产运行中 QHY 只产生无运动 WCS 见证，因此这套旧参数不再是启动条件；旧运行恢复仍必须按其原始预算解释，绝不能把未结 QHY 回程伪装成 G3 新运行。
- 目标入缝的每一个 outbound 分段必须另外预留一次回到该段实报起点的动作和等量累计位移。例如完整修正需要 4 个 outbound 分段时，`MaximumCorrectionAttempts=4` 不能证明任一分段失败后的安全回程，至少需要 5 次动作且累计门也要容纳当段回程；软件应在发令前给出 `SLIT_SEGMENT_RETURN_*_RESERVE_LIMIT`，不得边走边耗尽回程预算。
- outbound 与失败回程都必须在绝对坐标命令前写入带内容 SHA-256 的 durable intent 并保守预扣动作/位移；进程在命令接受后退出也不能漏账。schema 3 状态用 `BudgetLineageId` 把同一轮精调的 settled ledger 跨 run 串联，并以每个旧 run 的不可变 manifest 区分“未终结 handoff”和“已终结历史”；恢复只能收养唯一、非终结且 action config、设备身份、commissioning、目标、站点、地平线、Night Setup、历元和 pier side 全部一致的 lineage，同时继承累计位移、动作次数和最早开始时间。多 lineage、分叉计数、缺失/损坏 manifest 或终结 run 仍留有未结动作时保持阻断并由操作员显式接管。用于地平线判断的 mount/JNOW 坐标须先转换为 J2000，实际下发坐标仍保持驱动所需历元。

当前 G3 大步门与精细运动门都写入不可变运行配置哈希和 manifest。任一当前门无法预留回程、pier side 改变或当前地平线/安全门不通过时，真实自动流程必须在运动前阻断；已经离开原点时按 durable ledger 返回或安全暂停。QHY 解算失败只推进其曝光阶梯或报告失败，不授权 QHY 运动。不要从一次现场残差反推出机器无关默认值。

正式 commissioning evidence tool 只生成 schema 4 preset。其 schema 3
measurement definition 必须显式给出 `FineMotionAuthority` 和完整
`Phd2SlitPlacement`：安装纪元、profile/runtime identity 拓扑哈希、sensor/ROI、
binning、旋转权威、pier side、普通被导星与直导目标两套曝光、全部有界回程门、
fresh-frame/星点/狭缝提取门，以及完整版本化 calibration-quality policy 和与其
默认 JSON 字节一致的 SHA-256。工具会从锁定 PHD2 evidence 与这些字段重算 topology
fingerprint；同时必须给出四个轮位各自独立的 LED 宽度证据、经验不确定度、安装纪元和 detector geometry。不一致、复用证据、无法唯一分离、字符串枚举、缺字段或 plate-solve-only 旋转一律不写 preset。

`GhostAssistanceMode=Skip` 时 definition 必须显式写 `GhostAssistance=null`；
Auto/Require 则必须给出完整 schema-1 鬼影 calibration、match policy、source
extraction policy、三者哈希、runtime installation/optical/orientation fingerprint、
外部目录/WCS identity 门及独立 C11/G3 focus confidence 门。官方工具只调用普通、
确定性的点源提取与模板验证契约，不使用 LLM，也不从今晚一帧图像猜 calibration。
导入 `.bindings.json` 后 `ObservationUseRealMode`、`RealModeCommissioned`、
`AllowDegradedSupervisedScience` 仍为 false；鬼影模式只按已验证 definition 导入，
必须由操作员另行核对并显式授权真实运行。

## 4. N.I.N.A. 生产路径与影子模式

### 4.0 后端实测结果并入前端的强制门

完整规则见 [ADR-0009：单一生产观测路径与实测结果晋级](adr/0009-single-production-observation-route.md)。
从本节起，“后端测试成功”不再自动等于“自动观测可用”：

- N.I.N.A. Dockable 的“启动真实设备自动观测”和 Advanced Sequencer 的
  `OpenAstroSpec · UVEX4 目标观测`必须捕获同一 `RealRunConfiguration`、生成同一
  `ObservationPlan`、由同一 `RealObservationStageRunnerFactory` 创建 runner，并通过
  同一 `ObservationCoordinatorHost` 执行。前端只能是薄入口，不能再实现自己的目标移动、
  曝光阶梯、超时、失败恢复或清理顺序。
- 独立 PowerShell/.NET field harness 只可用于组件 commissioning、证据采集或紧急算法
  探索。即使它在真实天空从头跑到尾，其结论也只能写“commissioning 路线通过”；在对应
  分支移入生产 runner、回放同一不可变证据，并从正式前端重跑之前，不得写“前端已通过”
  或“一键自动观测已完成”。
- 每次后端成功必须立刻生成/更新一张 route-parity 表，至少逐项覆盖：入口与配置快照、
  目录指向、QHY 见证、G3 曝光阶梯/PL3、邻场恢复、N.I.N.A. 大步修正、PHD2 校准/选星/
  exact-lock、实时黑缝目的点、ATR 自适应曝光、QHY 并行采集、暂停/恢复、取消、异常清理和
  `FinalizeObservation`。每项必须标明共享实现、生产差异、测试和待验状态。
- field harness 发现的成功逻辑必须移入共享生产组件，不能只复制参数或在面板命令中再写
  一份。测试要断言 canonical stage、自动分支、恢复和最终终态；只断言 helper 返回值不算
  完整路线回归。
- 每次交付先用 replay/simulator 验证 Dockable 与 Advanced Sequencer 两个入口的配置和
  阶段同源，再按 [N.I.N.A. 插件 UI 发布检查](nina-plugin-ui-release-checks.md) 安装精确
  artifact。真实采集/运动/导星/恢复行为发生变化后，还必须在单独获得硬件授权时，从正式
  前端单次启动至少一次并运行到 `FinalizeObservation`。启动后若临时脚本、人工或大模型
  决定方向、目标点、曝光档或恢复分支，该 run 仍只算 commissioning。

没有天气窗口或硬件授权时允许交付源码修复，但状态必须准确写成“共享生产源已实现，前端
实机重放待完成”。禁止用后端成功掩盖尚未验证的前端路径，也禁止让用户到观测时才逐个
发现两条路线之间的差异。

动作配置 SHA-256 只冻结一次运行，不冻结后续测试。运行中修改超亮目标等动作分支不能
热替换进旧 runner；操作员可以结束旧运行后点击“按当前设置新开一轮”，由插件先审计退役
旧 G3 canonical 恢复链，再重新捕获当前设置的新哈希和新 run ID。这个操作不改写旧哈希，
也不要求把设置恢复成旧值；若存在多条含糊的未结运动 intent，仍保持阻断并要求单独检查。

当前高级序列容器只是共享生产 runner 的固定阶段外壳。中天翻转、同步测光/光谱拍摄规划
以及可编辑高级序列的未来职责拆分见
[N.I.N.A. Advanced Sequencer 真正耦合路线规划](nina-advanced-sequencer-coupling-roadmap.md)。
该文档是规划，不表示这些能力已经实现或验收。

### 4.1 台站配置方案与设备候选

日常观测不应逐项抄写工程字段，也不应为了“让插件看见设备”而把三台相机依次接入
N.I.N.A.。“自动准备”页提供与 N.I.N.A. Profile 类似的台站方案选择器：插件启动时
恢复并加载上次方案；本机默认项为“默认台站配置（大丰 UVEX4，自动发现）”。扫描与
加载都是只读文件操作，不打开相机、不启动 PHD2、不访问 COM5，也不移动设备。

候选严格按设备所有权从各自保存的配置中读取：

- 赤道仪和 ATR585M：N.I.N.A. 保存的 Profile 与既有 ATR 稳定 ID 绑定；
- G3M2210M：PHD2 Profile 的不可变注册表证据，包括 Profile、设备名和 USB 实例；
- QHYminiCam8M：独立 QHY 服务的 `ExpectedStableId`，N.I.N.A. 历史记录只作辅助候选。

四项在界面中均为中文下拉列表。选择候选只设置“期望身份”；真实运行开始后仍由
N.I.N.A.、PHD2 和 QHY 服务分别连接其唯一拥有的物理相机并复核实际身份。G3 和 QHY
不得为了填表接入 N.I.N.A.。切换 N.I.N.A. Profile 后插件重新扫描并按该 Profile 保存
的上次台站方案自动加载。

本机可在 `%ProgramData%\UVEX-ADV\commissioning\station-profiles\default.station-profile.json`
放置 schema 1 `OperationalTemplate`。大丰台的默认文件载入 2026-08-24/25 闭环实测过的
G3 曝光阶梯、驱动恢复等待、WCS/邻场搜索、QHY 粗居中及亮目标证据新鲜度/残差运行模板，
用于消除值为 0 的空表；亮目标例外分支的启用开关仍不在运行模板白名单中，必须由操作员
显式选择，模板不能借机授予例外权限；
这些台站数值不编译进源码，也不会成为其他开源部署的机器无关默认值。插件对模板字段采用
明确白名单，禁止它写入目标、设备身份、哈希或权限。自动发现方案会显式把
`RealModeCommissioned` 保持为 `false`，不会生成或猜测狭缝几何、Night Setup、光轴
转换、硬件指纹或 SHA-256，也不会替操作员授予运动权限。完整 `.bindings.json` 可作为
另一个台站方案导入；导入后会记住文件并在下次启动列入选择器。缺少的不可变证据仍是
真实缺项，但不会再与可自动发现的设备名称混成几十个手工输入框。

当保存项仍是首次安装的“自动发现”时，插件启动会在 `%ProgramData%\UVEX-ADV\commissioning`
中选择修改时间最新的完整 `.bindings.json`；一旦操作员明确选过某个正式方案，后续启动
继续恢复该方案，不会因为目录里生成了更新文件而暗中换掉。现场生成包是状态绑定、可
追溯的正式证据链，而不是把 `RealModeCommissioned` 写成 true 的机器本地捷径。它不因
跨过某个日历时间自动失效：只要设备所有者、稳定身份、安装纪元、USB/相机拓扑、ROI/
binning、焦点/UVEX 实报位置和内容 SHA-256 均未改变，安装标定可以跨夜继续使用；任一项
改变则在下一次动作或曝光前立即失效。旧文件中的 `ValidUntilUtc` 仅为向后兼容字段，不再
作为无状态的自动过期闹钟。当前帧、WCS、settle、安全快照、实时焦点评分等运行证据仍有
独立的短新鲜度门，不能拿“安装标定未过期”替代。

PHD2 路线也不再要求为赤道仪东西侧各维护一份重复包。preset 中的 `PierSide` 保留为校准
源侧和不可变 topology 哈希的来源；当前生产路线由 PHD2 独占 G3 和导星脉冲，PHD2 通过
ASCOM/aux-mount 连接处理过中天后的校准变换，runner 在当前侧创建新的 guide epoch，并以
fresh settle 和 fresh residual 重新授予 lock-shift。若翻转发生在尚未结清的 lock-shift/
回程 ledger 中，当前侧 topology 哈希变化会立即阻断，旧向量绝不跨侧解释。独立 G3
pixel-to-mount 矩阵、QHY→G3 预置模型和已启用的鬼影几何仍可能依赖 pier side；它们没有
双侧或实时 WCS 证据时继续失效，不从 PHD2 的变换外推。

UVEX M2 允许选择“保持当前位置（默认，不移动 M2）”。该模式仍在 schema-2 Night Setup
中锁定 M2 实际步数，并以 ATR 科学帧的谱线宽度作为当前质量证据，但把自动单步与累计
位移预算同时写为 `0`、逼近方向设为 `None`。它表示“沿用未改变且已经产生可用光谱的
位置”，不伪装成一次新的七点对焦。只有在质量证据失败、M2 实际位置改变或操作员明确
选择“已标定的七点谱线自动对焦”时，才允许进入会产生 M2 机械运动的路径。C11/Gemini、
GS350/AAF 与 UVEX/M2 仍是三个独立焦域，任何一个焦域的好结果都不能替另一个放行。

#### 4.1.1 “自动准备”向导与草稿边界

“自动准备”页把日常操作收敛为六组：观测目标、设备身份、一次性安装标定、本次观测
配置、目标狭缝和自动检查结果。这里必须区分三种文件，不能把它们都当成普通配置：

- 台站运行模板只恢复可公开复用的运行参数和各设备所有者保存的身份候选；它不授予运动
  权限；
- 完整安装标定包是安装期不可变证据，包含 schema-5 preset、硬件指纹、四槽独立 HDR
  狭缝证据、PHD2 证据、三焦域身份和运动限额，并通过 `.bindings.json` 一次导入；
- schema-2 Night Setup 是本次观测锁定快照，必须与完整安装标定相互绑定，且包含 C11
  主镜、GS350 广域镜和 UVEX M2 三个独立焦域的有效证据。

插件启动和“重新扫描保存的配置”只做文件盘点，不连接相机、PHD2、COM5 或赤道仪。
默认从完整 `.bindings.json` 中选择最后写入的一包；只有 preset、Night Setup、硬件指纹、
SHA-256、内部引用、设备身份和静态运行参数全部相互一致时，插件才自动把该包标记为
“静态校验已通过”。失败时保持未确认并显示问题，不退回到逐项手抄哈希。自动确认不等于
实时设备状态通过，也不授予运动或无人值守权限；点击启动后仍重新回读所有可用设备。
目标波段、波长标定参考、安全能力和级次分选滤镜改成中文选择项；设备身份仍从
N.I.N.A.、PHD2 与 QHY 服务的已保存配置下拉选择。点击“自动生成准备草稿”会把当前
目标、身份、相机参数、狭缝、光学位置、地平线和上述选择写入
`%ProgramData%\UVEX-ADV\commissioning\drafts\*.night-setup-draft.json`，并生成相邻
SHA-256。草稿使用独立的 `OpenAstroSpec.NightSetupPreparationDraft` 文档类型，明确列出
`UnresolvedItems`，绝不能被 Night Setup loader 当成锁定配置，也不能授予真实运动或
无人值守权限。

实测证据到齐后，用 commissioning 工具从草稿和原始证据生成 schema-2 Night Setup、
schema-5 preset 及完整 bindings。生成的新包放入本机 commissioning 目录后，下一次插件
启动会自动选择并校验最新完整包；仍可用“加载所选方案”显式切换到历史包。
自动检查只显示派生结果；不得让操作员逐项抄写 SHA-256、设备指纹或运动限额。历史
schema-1 Night Setup 可用于审计，但 UI 不再把它作为真实自动观测的合格配置导入。

当前台站默认选择“有人弱监督”。该选择必须同时写入 bindings、schema-5 preset 的
environment 要求和 schema-2 Night Setup 的 `SafetyCapability`：Safety Monitor、屋顶、天气、
镜盖四类 N.I.N.A. 适配器缺失或未连接时只产生醒目警告，不作为启动阻断；任何已经连接的
适配器若明确报告 unsafe、下雨、屋顶未开、镜盖关闭，仍然立即硬阻断。此模式要求操作员
全程可介入，绝不能标记为正式无人值守。切换到“N.I.N.A. 安全链”后四类实时回读重新全部
成为硬要求。

#### 4.1.2 AIWeather / RRCI 环境链与平移顶 commissioning

完整策略见 [ADR-0010](adr/0010-nina-environment-supervision-and-rolloff-roof.md)。当前台站只读
盘点得到的 N.I.N.A. Profile 选择为：

| 能力 | 锁定的 N.I.N.A. DeviceId | 实际职责 |
| --- | --- | --- |
| Safety Monitor | `AIWeatherSafetyMonitor` | 从 AI Weather Advanced 主节点取得安全判定 |
| Dome / Roof | `RRCIAdvanced.Dome` | 把实际平移顶映射为 N.I.N.A. shutter/roof |
| Weather Data | `NINA.OpenMeteo.Client` | 提供雨、云、风和湿度等可用指标 |
| Flat Device / Cover | `ASCOM.GeminiAutoCover.CoverCalibrator` | 主光路镜盖；不代表 QHY/GS350 镜头盖 |

生产 runner 在连接 ATR、赤道仪和 PHD2 owner 之前，先按运行快照自动连接上述已选择适配器；
这个连接阶段只读，不发开顶、关顶或镜盖运动命令。全无人监管随后按以下顺序工作：

1. 重新核验 action SHA-256、Profile 选择、commissioning、AIWeather safe、天气指标、目标
   全程地平线和赤道仪时钟；
2. 若平移顶关闭，先由 N.I.N.A. 停放赤道仪并回读 `AtPark && !Slewing`，再通过
   `IDomeMediator.OpenShutter` 委托 RRCI 开顶，并在有界时间内等待 `ShutterOpen`；
3. AIWeather 在运行中从 safe 变 unsafe 时，中止当前 ATR 曝光，并触发同一条故障收尾；
4. 正常结束或已开顶后的终端失败均按“停 QHY/PHD2/狭缝灯 → 关主光路镜盖 → 停放赤道仪
   → 关平移顶”执行并回读终态；同一 run 中屋顶开过后又关闭，不会自动重开。

RRCI 的 Replica 模式默认只是只读状态代理。要允许本机全无人路线发动作，必须在**唯一持有
旧 RRCI 驱动的 Primary 节点**显式启用“允许从节点控制屋顶”；远程开顶还必须另行启用
“允许从节点开顶”。Primary 与 Replica 必须共享有效凭据，状态必须新鲜，且 Primary 配置的
所有必需从节点都要提交新鲜的赤道仪已停放心跳。任一条件不满足时 runner 保留明确错误并
停止，不直连屋顶硬件、不猜测成功。首次启用按以下顺序验收，禁止一步跳到整夜无人运行：

1. 保持远程动作关闭，只连接并比对主/从节点屋顶状态；模拟断网，确认 Replica 在失效阈值
   后 `Connected=false`；
2. 只授权关顶，在屋顶本来关闭且赤道仪已停放时验证拒绝/幂等/终态回读；
3. 单独授权一次有界开顶，再验证 AIWeather unsafe 故障注入能执行镜盖→停放→关顶；
4. 最后从正式 Dockable 或 Advanced Sequencer 启动一条短观测并运行到
   `FinalizeObservation`。测试脚本或人工补动作不能替代这一步。

有人弱监管不发任何开关顶命令。四类设备分别按当前 Profile 选择降级：未选择、连接失败或
某项天气指标缺失只 warning，其他已连接能力继续使用；已连接设备明确报告 unsafe、雨、
超限云/风或屋顶关闭/错误仍阻断。若已选择可控镜盖，弱监管仍按需打开并在收尾关闭且必须
核验终态；未选择镜盖才 warning 降级，打不开或明确错误仍阻断。大丰近海台址的高湿度仅为
advisory，包括 100% 湿度本身也不等价于下雨或不安全。

质量门的统一语义见
[Gate severity and observation-continuity policy](design/gate-severity-and-continuity-policy.md)。
现场界面中的 `警告/继续` 必须自动推进并保留证据；只有 `错误/已暂停` 或
`错误/证据不足/已暂停` 才需要人工处理。QHY/GS350 并行测光链失败只降级为纯光谱观测，
ATR 单帧低 SNR/低对比度但未削顶时继续叠加。不要通过增加人工 Resume 来模拟 warning。

任何插件 XAML、Dockable ViewModel 或打包产物改变后，先执行
[N.I.N.A. 插件 UI 发布检查](nina-plugin-ui-release-checks.md)：完整构建、全部 UI
harness 场景、安装精确 artifact、真实启动 N.I.N.A. 并依次打开自动观测与校准库两个面板，
同时打开自动观测中的 ATR 单帧检查区，
最后确认新日志没有 XAML/Binding/dispatcher 未处理异常。只看到插件加载成功不算验收。

真实服务只公开明确标记为 `Hardware`、绑定本机 COM5/VID/PID 且与配置光栅线数一致的波长标定。历史模拟器条目（例如旧数据库中的 `sim-default` 600 lines/mm）会保留在数据库中用于追溯，但在真实模式 API 中不可见，也不能覆盖写入为实机标定。本机实机配置为 300 lines/mm；新建标定时必须重新测量步数/像素，不能把模拟器的 `10 steps/pixel` 当作初值。

1. 在 N.I.N.A. 中连接 ATR585M；PHD2 保持独占 G3M2210M。
2. 打开“OpenAstroSpec 自动观测 → 实时图像 → ATR 二维/一维光谱”，点击“绑定当前 ATR585M”，保存稳定 DeviceId。
3. 设置曝光、ROI、色散方向、提取孔径；保持“允许闭环电机运动”未选中。
4. 使用外部校准光源采集多组焦内/焦外图像，选择至少三条未饱和且 SNR>10 的谱线。
5. 用“采集一帧检查光谱”核对一维光谱、谱线像素位置和饱和比例；它只做单帧诊断，不启动自动观测流程。

PHD2/G3 亮目标现场选星不得把“最亮峰”或 PHD2 多星圆圈直接当作恒星身份
证明。自动化应优先复用成熟所有者功能：N.I.N.A. 负责目录指向、解析并居中，PHD2
负责全画幅原生自动选星、校准、导星和 runtime lock shift。协调器只对 PHD2 返回的
**同一个点**做否决式验证：在同一 fresh G3 帧中匹配恒星形态，并检查 SNR、紧致
FWHM、椭率、饱和、边缘、超亮目标光晕和黑色物理狭缝保护区；不再自行排序另一个
候选并替换 PHD2 的选择。原生结果被否决时必须停止该路线或进入显式退化路线。

亮目标仍在视场时，`AutoPreferDirectTargetThenOffSlit` 首先使用单独 commissioning
的最短曝光直接导目标；10 ms 是 PHD2 支持的合法档位，曝光改变后必须丢弃相机管线
中的首张旧曝光帧，再以 FITS 曝光读回验证新帧，不能把残留的 50 ms 光晕误判为
“10 ms 不可用”。目标不可直接稳定导星时才调用 PHD2 原生旁星选择。若同帧确实
没有独立星，可继续采用有人监督的短曝光直导目标，追踪大气抖动与目标在狭缝上下
漂移；不得把短曝光用作对焦证据。狭缝定位只认 LED/OFF 证据中的黑色不反光有限
孔径，最终残差为目标到该有限中心线的垂直/端点距离，不是到历史中点的欧氏距离。
完整现场结论见 [2026-08-23 实机调试收口](commissioning-night-2026-08-23.md)。

### 4.2 N.I.N.A. 原生目标、文件名与 FITS 溯源

自动观测第一次安装或切换 ATR Profile 后，必须在不连接设备的状态下完成以下检查：

1. 在“OpenAstroSpec 自动观测 → 高级设置 → N.I.N.A. 原生目标归档”查看当前模板；
2. 确认模板至少精确包含大小写敏感的 `$$TARGETNAME$$`；缺少时真实运行阻断；
3. 推荐使用 `$$DATEMINUS12$$\$$TARGETNAME$$\$$IMAGETYPE$$\$$DATETIME$$_$$TARGETNAME$$_$$EXPOSURETIME$$s_G$$GAIN$$_O$$OFFSET$$_$$FRAMENR$$`；不同布局只要保留目标令牌仍可运行，缺少 `$$IMAGETYPE$$` 作为可见建议而不是运动安全硬门；
4. 只有操作员检查“当前值”和“推荐值”后，才点击“应用推荐的目标分目录模板”；本次 N.I.N.A. 会话可用相邻撤销按钮恢复。不得在插件加载或真实运行启动时静默改写 Profile；
5. 在高级序列中确认 `OpenAstroSpec · UVEX4 目标观测` 能被 N.I.N.A. 识别为原生目标容器，目标名和 J2000 坐标修改后仍与兼容字段一致；
6. 使用模拟/离线 FITS 验证器先检查 `OBJECT`、`OBSRUNID`、`UVEXSTG`、`UVEXCID`、`NIGHTSET`、`IMAGETYP` 和可选 `CATALOG`。真实保存后插件只读重开同一绝对路径；任何不一致均发布 `ATR_FITS_PROVENANCE_MISMATCH`，保留原始 FITS，不重命名、不移动、不改写，也不计为已接受科学帧。

旧文件不会被追溯重排。若旧文件名没有目标，优先用当次 run manifest 建立跨相机关联；缺少 manifest 时只能读取 FITS `OBJECT` 和坐标。`OBJECT` 只保存稳定科学目标名，不得再拼入 run、probe/science、重试或帧号。完整约束见 [ADR-0007](adr/0007-nina-native-target-and-image-provenance.md)。

### 4.3 ATR585M 温控与收口

图谱 [ATR585M 官方手册](https://www.touptek-astro.com/dl_manual/ATR585M_en.pdf)
说明该机使用双级 TEC、风扇和 PID 直接调节到目标温度，没有规定必须按时间斜坡升降温。
因此本项目默认策略为：

1. 由 N.I.N.A. 唯一拥有 ATR585M；真实自动流程在精确身份核验后立即直接设定 Night Setup
   中的目标温度，不要求操作员先手工制冷；
2. ATR 预冷与指向、解析、入缝、导星和 QHY 同步测光准备并行。室温状态只显示
   `ATR_PRECOOLING_IN_PROGRESS`，不再被误判成 Night Setup 配置错误；
3. 第一张 ATR 试曝光前才等待实际 `Temperature`、实际 `TemperatureSetPoint`、
   `CoolerOn` 和制冷功率形成连续一致证据；等待期间 PHD2 与已启动的 QHY 测光保持运行；
4. 只有实际温差连续落在版本化容差内才允许 ATR 快门打开；Advanced API 2.2.15.2 的
   `AtTargetTemp` 只是严格相等派生值，只写审计，不参与放行；
5. 任何掉线、DeviceId 改变、设定点未生效、制冷关闭或遥测不一致都会在 ATR 曝光前暂停，
   但不会倒退成要求人工完成正常预冷；
6. 收口时先确认无曝光，关闭 TEC 并由 N.I.N.A. 正常断开；默认不执行多分钟阶梯回温；
7. 若未来某一机型、驱动或现场确有斜坡要求，必须作为显式、可见、可关闭的设备策略和
   commissioning 证据加入，不能把它偷偷硬编码成所有现代制冷相机的共同要求。

“连续真实温度稳定”是科学一致性门，不是保护相机所需的阶梯动作。当前异常遥测恢复与
审计方法见 [ATR585M 制冷恢复](atr585m-cooling-recovery.md)。

## 5. 开放闭环

1. 若本夜确需移动 M2，选择“已标定的七点谱线自动对焦”，再设置 7 点扫描步长、M2 上下限和同向逼近回差；通常沿用已验证位置时保持“保持当前位置”，这些运动参数会禁用。
2. 设置参考线像素、目标像素和实测 `gratingStepsPerPixel`。
3. 单独确认 M2 自动对焦授权；“保持当前位置”不会授予任何 M2 自动运动。
4. 先执行谱线自动对焦，再执行波长锁定；失败时确认机构已退回初始位置。
5. 对焦结果应落在完整扫描最优点的一个采样步长内；波长锁定需连续两帧达到 ±0.25 pixel。

## 6. GS350/QHY 到 C11/G3 的可选预置转换

两套光路的当前光轴差不是设备常数，禁止写进源码、机器无关默认配置或无 provenance 的 seed。热胀冷缩、非同步形变、赤道仪误差、相机旋转、拆装和重新同轴都可能改变它；迁移到另一套设备时必须从空记录开始。这里的 `QhyToG3Transfer` 只用于 QHY 无运动见证后的可选预置移动，不能与最终入缝所需的 `G3PixelToMount` 像素→赤道仪矩阵共用字段或证据。

1. 先验收不依赖两镜转换的 `Skip` 路径：选择高空亮星，保留 QHY 无运动正式 WCS 见证后直接取得 G3 全帧并精解析。目标在画外时走 N.I.N.A. 有界大步修正和 fresh G3 复核；G3 无正式解时使用预先锁定的有重叠邻场搜索，每步重拍、重解，并受单步、半径、累计位移、次数、耗时、地平线和安全返回限制。成功后再用 PHD2 exact-lock 或独立 G3 像素→赤道仪模型闭环入缝。
2. 只有在上述回退路径可重复工作后，才从同一时段、同一安装纪元的 QHY 与 G3 解算对拟合预置模型。`QhyG3FastPairEnabled` 根据 2026-08-28 大丰实机结果默认启用：每次 G3 WCS 成功后、任何后续移动前，优先复用同一 mount binding 下的新鲜 QHY WCS；缓存无效时只拍一张显式短曝光并只解算一次。五个 mount readback 必须证明两幅 WCS 属于同一指向。该步骤只生成 `MotionAuthority=false` 的单样本 Candidate，不授权跨镜移动；Candidate 除随 run evidence 保存外，还会自动写入 `%LocalAppData%\UVEX-ADV\calibration\qhy-g3\<hardware-fingerprint>\` 的不可变候选档案并原子更新 `latest-candidate.json` 索引，因此不再要求把东/北偏移人工抄进 Profile。索引仍不是 Active 运动权限。操作员可以在高级设置中关闭快速配对并点击“保存当前高级设置”立即写入当前 N.I.N.A. Profile。完整时序与证据见 [QHY / G3 快速同指向双解算](qhy-g3-fast-solve-pair.md)。至少记录两相机和两光学系统标识、赤道仪、ROI/binning/方向、pier side、参考天区、时角/赤纬/高度范围、温度、UTC、样本数、系数单位、残差/协方差和原始证据哈希。
3. 为每次安装建立显式 `InstallationEpochId`。拆卸、重装、重新同轴、准直或相机旋转后换新 ID，旧记录立即不适用，不能仅靠相同 USB DeviceId 继续使用。
4. 设置 `ValidFromUtc`、`ValidUntilUtc`、最大预测不确定度和最大预置移动。分别在适用范围边缘、不同温度/姿态和两个 pier side 验证；不能把一侧或一次测量外推成全局常数。
5. 在工作流界面验证三种模式：默认 `AutoIfValidElseSkip`、显式 `Skip` 和专家用 `RequireValid`。界面必须显示有效记录、系数/单位、年龄、适用范围、fingerprint、不确定度、预测移动和使用/跳过理由；编辑或人工覆盖生成带作者、时间、原因的新版本，不改写旧记录。
6. 对 `AutoIfValidElseSkip` 注入过期、fingerprint 改变、安装纪元改变、pier side 不符、温度/姿态越界、高不确定度和超运动上限条件。每种情况都应在任何中间移动之前记录 `TransferSkipped`，随后自动进入 G3 直接解算/有界搜索，而不是暂停或使用历史常量。
7. 对有效记录执行一次有界预置移动，立即用新 G3 解算验证预测残差。残差超门时将该记录判为不适用并保留证据；成功解算对可产生新的候选样本/版本，但不能原地更新正在运行的记录。
8. 子午线翻转后重新判定 pier side；没有匹配记录就跳过预置移动。完整验证 manifest 同时保留预置模式、记录 ID/哈希、适用性结论、预测/实发移动、后验残差，以及 G3 搜索的原点、全部步次和最终位置。

当前 schema 4 commissioning preset 中的 `MountTransform` 是 **G3 像素→赤道仪** 的入缝模型，不是 QHY→G3 光轴关系。runner 已能生成版本化 `QhyToG3TransferCandidate` 并在 UI 中配置快速配对，但尚未提供经多样本独立验证的 Active 记录导入/激活路径；因此实机运行仍必须将可选中间预置阶段保持为 `Skip`。不得把 Candidate、现有 `MountTransform` 或人工记忆的偏移挪作代用。

### 无机械零位/人工设零后的解算提示语义

ST25 非 Pro 没有可靠机械零位时，重新上电后人工设置的赤道仪绝对坐标可能与真实天空相差很多度。只要本轮 QHY 接受帧、正式 PL3 解、文件/解算 SHA-256、前后 mount binding、pier side 和当前未移动状态仍全部新鲜，G3 长曝光解算会直接采用该 **QHY 正式 WCS 作为天空提示**；不再拿错误的目标/赤道仪提示去否定一个与 QHY 相符的 G3 正式解。这里必须区分三种量：

- `QHY WCS ↔ 目标坐标` 的大残差表示当前实际指向/赤道仪绝对坐标问题；
- 同一未移动指向的 `G3 WCS ↔ QHY WCS` 才是两镜光轴测量；
- `G3 像素 ↔ 狭缝` 及 `G3PixelToMount` 属于最终入缝闭环，不能与前两者共用。

采用 QHY WCS 时，若它与赤道仪自报坐标的差异不超过 `G3MaximumPlateSolveHintOffsetDegrees`，只改变 PL3 提示，不 Sync；若超过，则把这个大差异解释成无机械零位赤道仪的绝对坐标失配，在静止、未停放、非 pulse guiding、pier side 未变且所有文件/解算/mount binding 证据均新鲜时，每轮最多通过 N.I.N.A. telescope mediator 执行一次 `Sync`，并要求 5 arcsec 内的新读回。Sync 本身发出零 Slew；读回通过后，程序在地平线和即时设备门通过时重新执行一次原计划目录转向，等待稳定实报，并只让下一张 fresh G3 WCS 证明光学到位。不给予 QHY 后续精修/搜索运动权限，也不会在 QHY/G3 之间反复切换坐标。若 QHY 证据不新鲜、文件哈希改变、赤道仪在两帧间移动或 pier side/历元改变，则自动退回目录目标提示并在审计日志中写明原因。`G3MaximumPlateSolveHintOffsetDegrees` 因而是“G3 相对所选天空提示的可信范围”和“一次性绝对坐标恢复触发阈值”，不是可以用一次 19° 目标/赤道仪残差自动扩大的光轴配置。

## 回滚

在管理器中进入维护模式或停止兼容名称仍为 `UVEX-ADV` 的 Windows 服务，确认 COM5 已释放后再启动 DRIVER.UVEX4。不要同时运行两个控制端。
