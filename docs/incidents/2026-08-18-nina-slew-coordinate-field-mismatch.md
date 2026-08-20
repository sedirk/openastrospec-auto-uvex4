# 2026-08-18 N.I.N.A. 粗回中坐标字段错配事故复盘

- **状态：** 已停止错误动作、恢复目标区并完成新鲜帧复核；源码防复发措施已加入，尚未以本事故证明完整自动化流程验收。
- **发生时间：** 2026-08-18 22:22:36 CST（2026-08-18 14:22:36 UTC）
- **恢复时间：** 约 2026-08-18 22:26 CST；2026-08-18 22:27:55 CST 取得恢复后的新鲜 QHY 帧。
- **范围：** 一次现场临时 PowerShell 粗回中片段；不是 `UvexAdv.Nina.Plugin` 的正式赤道仪发令路径。
- **严重度：** 高。计划内约 308.86 角秒的小修正变成约 45° 的赤纬转向，越过了预期动作边界。

## 1. 发生了什么

G3 的 10 秒直接解析已经成功，但 Deneb 位于 G3 画面外，解算中心到目录目标的残差约为东向 +136.63″、北向 +276.99″。现场临时片段准备通过 N.I.N.A. Advanced API 执行一次反向粗修正。

坐标转换 helper 的 schema 是 `uvex-adv.nina-coordinate-transform.v1`，J2000 节点的字段是：

```text
J2000.RaDegrees
J2000.DecDegrees
J2000.Epoch
```

临时片段却读取了不存在的字段：

```powershell
$baseDec = [double]$conv.J2000.Dec       # 错误
$baseDec = [double]$conv.J2000.DecDegrees # 正确字段
```

该片段未在这个访问边界启用 `Set-StrictMode -Version Latest`，也没有先通过 `PSObject.Properties[...]` 验证字段存在。普通 PowerShell 将缺失属性读成 `$null`，而 `[double]$null` 又静默得到 `0`。随后北向修正被加到 `0°`，形成约 `−0.07694° J2000` 的命令赤纬，而不是在 Deneb 附近的 J2000 赤纬上做 276.99″ 修正。

请求在调用方 30 秒等待窗口内没有返回。没有重发请求；随后的只读实报显示赤道仪已停止转动并保持跟踪，位置约为 RA `310.8465625° JNOW`、Dec `+0.0209425° JNOW`，高度约 `55.894°`。这与错误的近零 J2000 赤纬命令在当前历元下相符。PHD2 当时处于停止状态；本事故没有请求屋顶动作。

现场随后使用明确的 Deneb J2000 目录坐标恢复目标区，并以新鲜帧和新解算继续复核。超时期间的命令接受状态始终按“歧义、可能已执行”处理，而不是按失败自动重试。

## 2. 影响与未发生事项

- 赤道仪执行了远大于计划边界的转向，Deneb 暂时离开 G3/QHY 预期目标区；当时的转换标定和局部回程假设随之失效。
- 该动作不应计作一次有效的粗居中样本、G3 像素→赤道仪变换样本或目标入缝证据。
- 没有把错误值写入机器无关默认配置，也没有把它固化成两镜光轴差常量。
- 没有自动重试歧义请求，没有启动导星或科学曝光来掩盖错误位置。
- 本次源码复盘只读检查现场证据并运行离线测试；没有连接、移动或重新配置任何设备。
- 原始 FITS、解算输出和日志保持原样，没有重写、重命名、移动或删除。

## 3. 证据

事故前最近的 G3 新鲜帧和解算：

- `output/commissioning/2026-08-18-night/evidence/g3-Deneb/direct-recovery-20260818T142111250Z/deneb-g3-recovery-10s-20260818T142111250Z.fit`
  - SHA-256 `C1424427FE5B41DCC4F7C1AF78BF792E46F167C0605565EB7BD44F70B113AF43`
- `output/commissioning/2026-08-18-night/evidence/g3-Deneb/deneb-g3-recovery-solve-20260818T142111250Z_PS3.txt`
  - SHA-256 `C1ECFA54D97AEE6DE0CD06953F7B1AE7D48DAB6D07198F83FA909C2784F34438`
- 该 G3 FITS 的 `DATE-OBS` 为 `2026-08-18T14:21:11.437Z`，设备实报头含 RA `310.773°`、Dec `45.02269°`。

恢复后的新鲜 QHY 帧和解算：

- `output/commissioning/2026-08-18-night/evidence/qhy-Deneb/deneb-qhy-recovery-20260818T142755Z.fits`
  - SHA-256 `93A449A26AEF368B28DBF874D314FC04561488955D5DAC4D2FF24D7CA6AC0C8B`
- `output/commissioning/2026-08-18-night/evidence/qhy-Deneb/deneb-qhy-recovery-20260818T142755Z_PS3.txt`
  - SHA-256 `9E201D913CF1261B42BD6395D0E9B1EA2733837BE376358F568C027C7416132E`
- QHY FITS 的 `DATE-OBS` 为 `2026-08-18T14:27:55.5510123Z`，请求目标元数据为 `Deneb`。

这些文件位于忽略的本机 `output/` 证据树，不进入 Git。N.I.N.A. 运行日志同样属于本机运行证据，不应作为源码提交。

## 4. 根因

直接根因是动态 JSON 字段名错配：helper 输出 `DecDegrees`，调用片段读取 `.Dec`。PowerShell 的缺失属性和数值强转组合把一个结构错误降格成了合法数值零。

事故还暴露了五个防线缺口：

1. 临时硬件脚本没有强制 StrictMode，也没有“字段存在后才转换”的解析器。
2. 动态 `PSCustomObject` 跨过了坐标边界，没有立即变成一个带单位和历元的强类型坐标。
3. 运动门只核对了计划修正向量的大小，没有在发令前用最新实报坐标重新计算“最终绝对命令”的实际角距离。基准坐标错误后，小修正门仍可能表面通过。
4. 精确 URL 没有作为独立的、可核对且带 SHA-256 的请求预览保存；字段错误没有在最终命令层变得显眼。
5. 现场一次性片段绕过了正式插件已有的强类型 `NINA.Astrometry.Coordinates`、有限值/范围检查、动作预算、持久 intent 和实报验收设计。

## 5. 强制防复发规则

以下规则适用于任何可能形成赤道仪绝对坐标命令的正式代码、commissioning harness 或现场 PowerShell。它们是叠加防线，不能相互替代。

### 5.1 解析边界

1. 脚本必须在任何输入访问之前执行 `Set-StrictMode -Version Latest`，并设置终止型错误策略。
2. 动态 JSON 的每个必需字段必须用 `PSObject.Properties['ExactName']` 验证存在且非 `null`，然后才能转换。禁止 `[double]$object.path` 这种把字段发现和类型转换合并的写法。
3. 数值必须是 JSON number；不接受字符串数字、布尔值或隐式空值。RA、Dec 和往返误差必须有限。
4. RA 必须在 `[0°, 360°)`，Dec 必须在 `[−90°, +90°]`。历元必须是显式枚举/精确字符串；JNOW→J2000 转换还必须通过有上限的往返误差门。
5. 解析成功后立即生成一个强类型坐标，字段名称必须包含单位，例如 `RightAscensionDegrees`、`DeclinationDegrees` 和 `Epoch`。后续函数只接受该类型，不再接受原始动态对象。

### 5.2 命令形成与证据

1. 使用固定、钉死的 loopback endpoint 和 invariant-culture 数字格式，通过 URI 编码器形成规范请求；禁止从未验证的字符串拼接主机、路径或坐标。
2. 发令前生成精确请求预览，至少显示 operation ID、RA、Dec、历元、最新实报起点、预计切平面偏移、球面角距离、pier side、endpoint、请求 SHA-256 和上游转换输出 SHA-256。
3. 运动门必须同时限制：计划偏移向量、最新实报到最终绝对命令的球面角距离、单步/累计动作、回程预留、地平线、安全状态、历元和 pier side。任一值非有限、过期或不一致即阻断。
4. 请求发送前先持久化不可变 intent；intent 内容哈希、operation ID、预扣动作次数/位移和最终绝对坐标必须落盘。持久化失败不得发令。
5. N.I.N.A. 的此类 GET 动作端点本身不提供可依赖的幂等键，因此调用侧必须是“单次发令、零自动重试”。超时、断线或响应丢失都进入 `ambiguous/pending-reconciliation`，先读实报和日志再决定下一步。

### 5.3 接受与恢复

1. HTTP 成功或 `waitForResult` 返回都不是到位证据。必须取得命令之后时间戳的新鲜赤道仪实报，复核 connected、tracking、not slewing、RA/Dec、历元、pier side 和到命令位置的残差。
2. 实报缺失、时间早于 intent、历元改变、pier side 改变或到位残差超门时，不得接受该步，也不得开始下一次运动、导星或曝光。
3. 歧义命令的恢复从最新实报重新规划；不得盲目重发旧绝对命令。恢复后必须取得新鲜图像/解算，旧的局部变换和目标入缝证据按过期处理。
4. 一次性控制台片段不得直接操作赤道仪。现场试验应使用受版本控制、默认只读、带离线自检和上述证据链的 bounded harness，或直接使用正式 N.I.N.A. mediator 路径。

## 6. 源码审计与已落实改动

- `ObservationTargetImportNinaSources` 使用 N.I.N.A. 的强类型 `Coordinates` 做历元转换，并在复制到计划 DTO 后执行有限值和范围检查；没有 helper `.Dec`/`.DecDegrees` 混用。
- `RealObservationStageRunner` 的正式发令边界使用强类型 `Coordinates`，发令前检查 RA/Dec 有限且在范围内，并通过 N.I.N.A. telescope mediator 发令；没有把动态 helper JSON 直接拼成 URI。本次没有编辑该文件，以免与正在进行的入缝恢复实现冲突。
- QHY 请求的 RA/Dec 是一对 nullable typed fields；服务端要求二者同时出现并验证有限值/范围。
- `ObservationPlan.Validate()` 原来只检查范围，`NaN` 会绕过比较。本次已补为“finite + range”，并添加 RA/Dec 的 NaN、正负无穷、越界和合法边界测试。
- 新增 `scripts/coordinate-command-safety.psm1`：它只负责严格解析 helper envelope 和生成规范、带哈希、禁止自动重试的请求预览；模块不包含任何 HTTP/设备发令实现。
- 新增 `scripts/test-coordinate-command-safety.ps1`：离线复现 `[double]$null -eq 0`，验证错误 `.Dec` 不能代替 `DecDegrees`，覆盖 null、字符串数字、NaN、越界、错误历元、往返误差、区域格式、endpoint pin、operation ID 和哈希确定性；同时阻止未来受版本控制的 PowerShell 直接构造 mount slew endpoint 而不启用 StrictMode/预览 guard。
- `scripts/build.ps1` 已把上述纯离线安全测试纳入常规构建。
- 当前 `tmp/g3-mount-transform-probe/` 被 `.gitignore` 排除，不是产品发布内容。审计时其中转换 helper 和探针脚本已使用 `J2000.DecDegrees`；但任何 ignored 临时文件都不能被当作长期安全控制或源码验收依据。
- 冻结的 baseline 与 ADR-0001 未修改。

## 7. 关闭条件

本事故的软件防复发项只有在以下条件全部满足后才能标记关闭：

1. 设计基线校验、PowerShell 坐标安全测试、Observatory 定向测试和完整 Release 构建全部通过。
2. 下一次实机 commissioning 使用正式路径或受版本控制的 bounded harness；发令前证据中能看到最终绝对坐标、实际角距离、request SHA-256 和 operation ID。
3. 注入缺失 `DecDegrees`、`null`、字符串、NaN、越界、错误历元、超时和响应丢失时，全部在发令前阻断或进入无重试的歧义协调状态。
4. 一次受控小步测试以命令后新鲜实报证明到位，并以新鲜图像/解算闭环；不得用本次恢复本身替代该验收。
