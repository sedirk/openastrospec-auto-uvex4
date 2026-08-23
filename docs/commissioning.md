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

赤道仪有两套用途不同、不得共用数值的运动门：

- `QhyCoarseCenteringLimits` schema 1 只管 GS350/QHY 广域 WCS 粗居中。目录转向后的正常残差可能达到数百角秒，因此必须根据实测转向误差、赤道仪响应和安全回程单独设置单步、累计量、动作次数和耗时。软件可把一次大残差拆成多个不超过粗门单步上限的 WCS 闭环修正；每一步都必须有新 QHY 解算、当前地平线/安全门，并在发令前预留分步回到该轮粗居中原点的累计量和次数。
- commissioning preset 的 `Motion` 只管 G3 本地搜索和目标入缝等精细修正。不得为了容纳一次 856 arcsec 级的 QHY 粗指向残差而放宽这套精细门，也不得让 QHY 粗居中消耗或复用 G3 像素→赤道仪转换。
- 目标入缝的每一个 outbound 分段必须另外预留一次回到该段实报起点的动作和等量累计位移。例如完整修正需要 4 个 outbound 分段时，`MaximumCorrectionAttempts=4` 不能证明任一分段失败后的安全回程，至少需要 5 次动作且累计门也要容纳当段回程；软件应在发令前给出 `SLIT_SEGMENT_RETURN_*_RESERVE_LIMIT`，不得边走边耗尽回程预算。
- outbound 与失败回程都必须在绝对坐标命令前写入带内容 SHA-256 的 durable intent 并保守预扣动作/位移；进程在命令接受后退出也不能漏账。schema 3 状态用 `BudgetLineageId` 把同一轮精调的 settled ledger 跨 run 串联，并以每个旧 run 的不可变 manifest 区分“未终结 handoff”和“已终结历史”；恢复只能收养唯一、非终结且 action config、设备身份、commissioning、目标、站点、地平线、Night Setup、历元和 pier side 全部一致的 lineage，同时继承累计位移、动作次数和最早开始时间。多 lineage、分叉计数、缺失/损坏 manifest 或终结 run 仍留有未结动作时保持阻断并由操作员显式接管。用于地平线判断的 mount/JNOW 坐标须先转换为 J2000，实际下发坐标仍保持驱动所需历元。

两套门都写入不可变运行配置哈希和 manifest。粗门任一字段为 0、schema 不符、无法预留回程、QHY 新帧解算失败、pier side 改变或当前地平线/安全门不通过时，真实自动流程必须在运动前阻断；若已经离开粗居中原点，则先在仍然安全时按粗门分步返回并保留每一步证据。不要从一次现场残差反推出机器无关默认值。

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

## 4. N.I.N.A. 影子模式

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

### 4.1 ATR585M 温控与收口

图谱 [ATR585M 官方手册](https://www.touptek-astro.com/dl_manual/ATR585M_en.pdf)
说明该机使用双级 TEC、风扇和 PID 直接调节到目标温度，没有规定必须按时间斜坡升降温。
因此本项目默认策略为：

1. 由 N.I.N.A. 唯一拥有 ATR585M，并直接设定 Night Setup 中的目标温度；
2. 等待实际 `Temperature`、实际 `TemperatureSetPoint`、`CoolerOn` 和制冷功率/趋势形成连续一致证据；
3. 只有实际温差连续落在版本化容差内才允许科学块；Advanced API 2.2.15.2 的
   `AtTargetTemp` 只是严格相等派生值，只写审计，不参与放行；
4. 收口时先确认无曝光，关闭 TEC 并由 N.I.N.A. 正常断开；默认不执行多分钟阶梯回温；
5. 若未来某一机型、驱动或现场确有斜坡要求，必须作为显式、可见、可关闭的设备策略和
   commissioning 证据加入，不能把它偷偷硬编码成所有现代制冷相机的共同要求。

“连续真实温度稳定”是科学一致性门，不是保护相机所需的阶梯动作。当前异常遥测恢复与
审计方法见 [ATR585M 制冷恢复](atr585m-cooling-recovery.md)。

## 5. 开放闭环

1. 设置 7 点扫描步长、M2 上下限和同向逼近回差。
2. 设置参考线像素、目标像素和实测 `gratingStepsPerPixel`。
3. 勾选“调试完成，允许闭环电机运动”。
4. 先执行谱线自动对焦，再执行波长锁定；失败时确认机构已退回初始位置。
5. 对焦结果应落在完整扫描最优点的一个采样步长内；波长锁定需连续两帧达到 ±0.25 pixel。

## 6. GS350/QHY 到 C11/G3 的可选预置转换

两套光路的当前光轴差不是设备常数，禁止写进源码、机器无关默认配置或无 provenance 的 seed。热胀冷缩、非同步形变、赤道仪误差、相机旋转、拆装和重新同轴都可能改变它；迁移到另一套设备时必须从空记录开始。这里的 `QhyToG3Transfer` 只用于 QHY 粗居中后的可选预置移动，不能与最终入缝所需的 `G3PixelToMount` 像素→赤道仪矩阵共用字段或证据。

1. 先验收不依赖两镜转换的 `Skip` 路径：选择高空亮星，完成 QHY 粗解析和居中后直接取得 G3 全帧并精解析。若 G3 无足够星点，使用预先锁定的小步方形/螺旋搜索；每步重拍、重解，并受单步、半径、累计位移、次数、耗时、地平线和安全返回限制。成功后再用独立的 G3 像素→赤道仪模型闭环入缝。
2. 只有在上述回退路径可重复工作后，才从同一时段、同一安装纪元的 QHY 与 G3 解算对拟合预置模型。可启用 `QhyG3FastPairEnabled`：每次 G3 WCS 成功后、任何后续移动前，优先复用同一 mount binding 下的新鲜 QHY WCS；缓存无效时只拍一张显式短曝光并只解算一次。五个 mount readback 必须证明两幅 WCS 属于同一指向。该步骤只生成 `MotionAuthority=false` 的单样本 Candidate，完整时序与证据见 [QHY / G3 快速同指向双解算](qhy-g3-fast-solve-pair.md)。至少记录两相机和两光学系统标识、赤道仪、ROI/binning/方向、pier side、参考天区、时角/赤纬/高度范围、温度、UTC、样本数、系数单位、残差/协方差和原始证据哈希。
3. 为每次安装建立显式 `InstallationEpochId`。拆卸、重装、重新同轴、准直或相机旋转后换新 ID，旧记录立即不适用，不能仅靠相同 USB DeviceId 继续使用。
4. 设置 `ValidFromUtc`、`ValidUntilUtc`、最大预测不确定度和最大预置移动。分别在适用范围边缘、不同温度/姿态和两个 pier side 验证；不能把一侧或一次测量外推成全局常数。
5. 在工作流界面验证三种模式：默认 `AutoIfValidElseSkip`、显式 `Skip` 和专家用 `RequireValid`。界面必须显示有效记录、系数/单位、年龄、适用范围、fingerprint、不确定度、预测移动和使用/跳过理由；编辑或人工覆盖生成带作者、时间、原因的新版本，不改写旧记录。
6. 对 `AutoIfValidElseSkip` 注入过期、fingerprint 改变、安装纪元改变、pier side 不符、温度/姿态越界、高不确定度和超运动上限条件。每种情况都应在任何中间移动之前记录 `TransferSkipped`，随后自动进入 G3 直接解算/有界搜索，而不是暂停或使用历史常量。
7. 对有效记录执行一次有界预置移动，立即用新 G3 解算验证预测残差。残差超门时将该记录判为不适用并保留证据；成功解算对可产生新的候选样本/版本，但不能原地更新正在运行的记录。
8. 子午线翻转后重新判定 pier side；没有匹配记录就跳过预置移动。完整验证 manifest 同时保留预置模式、记录 ID/哈希、适用性结论、预测/实发移动、后验残差，以及 G3 搜索的原点、全部步次和最终位置。

当前 schema 4 commissioning preset 中的 `MountTransform` 是 **G3 像素→赤道仪** 的入缝模型，不是 QHY→G3 光轴关系。runner 已能生成版本化 `QhyToG3TransferCandidate` 并在 UI 中配置快速配对，但尚未提供经多样本独立验证的 Active 记录导入/激活路径；因此实机运行仍必须将可选中间预置阶段保持为 `Skip`。不得把 Candidate、现有 `MountTransform` 或人工记忆的偏移挪作代用。

## 回滚

在管理器中进入维护模式或停止兼容名称仍为 `UVEX-ADV` 的 Windows 服务，确认 COM5 已释放后再启动 DRIVER.UVEX4。不要同时运行两个控制端。
