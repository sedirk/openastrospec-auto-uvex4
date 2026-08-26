# 2026-08-24/25 实机收口：从 Deneb commissioning 到两次无人干预闭环与暗目标邻场验证

**本地观测夜：** 2026-08-24（Asia/Shanghai）  
**现场条件：** 有月光的晴夜，间歇薄云；屋顶由操作员人工开启，台址四周约 40° 遮挡  
**目标：** Deneb / 天津四、Mirfak / 天船三、Algol / 大陵五、HD 19445（V≈8.06）  
**结论：** 本夜先在 Deneb 上由人工/大模型监督完成 N.I.N.A. 原生居中、PHD2 原生选星/校准、fresh 黑缝中点、导星及 ATR/QHY 并行采集，并据此修正 PlateSolve3 方向、缝端点判定和 ATR 迹线过曝门。随后普通确定性程序分别在 Mirfak 和 Algol 上从一次启动跑完“目录转向 → QHY 见证 → G3 双解闭环 → N.I.N.A. 大步移动 → PHD2 原生选星/精确入缝/持续导星 → 自适应 ATR + 配对 QHY 科学帧”，两次均明确记录 `NoManualOrModelCorrectionAfterSingleStart=true`。最后对 HD 19445 做暗目标/稀疏场挑战；晨光迅速恶化使半小时窗口耗尽，未完成闭环，但取得一份 5′ 东邻场的低匹配正式 WCS 和多份失败对照，为邻场重叠路线提供了实证。屋顶始终由操作员负责，不在无人值守声明内。

原始 FITS、PHD2 帧、审计与 QHY manifest 位于 Git 忽略的本机观测目录，没有被重命名、移动、重写或提交。本文只固化可复用的软件结论和经过哈希的本机证据引用；单夜像素坐标不是机器无关常量。

## 1. N.I.N.A. 原生指向并居中

本轮没有重新实现常规“解析—修正—再解析”算法。N.I.N.A. 3.2 的原生 `NINA.PlateSolving.CenteringSolver` 负责循环；OpenAstroSpec 只提供 QHY service 的不可变采集/解算帧，并在 N.I.N.A. 每次请求赤道仪修正时继续执行项目已有的单步、累计量、次数、时间、地平线、pier side、fresh mount binding 和回程预留门。设备所有权不变：QHYminiCam8M 仍只由 QHY service 打开，赤道仪仍只经 N.I.N.A. mediator 发令。

实测原生循环的目标—中心残差由约 `21′47″` 改善到约 `18″`，再到约 `6″`；独立 QHY 最终解算残差为 `5.88″`，通过 `15″` 门。N.I.N.A. 日志明确记录原生 Center 的逐轮 solve/correct/repeat。独立 Advanced API 动作在完成后曾返回笼统的 `Slew failed`，但该布尔结果与日志及 fresh QHY WCS 不一致；生产适配器不以 API 文本代替 fresh WCS 终验。

关键本机证据：

- `output/commissioning/2026-08-24-night/evidence/qhy-deneb-pre-nina-native-20260824T115532Z/`
- `output/commissioning/2026-08-24-night/evidence/qhy-Deneb/nina-native-final-20260824T120043Z.fits`

## 2. PHD2 原生选星、校准和错误动作回程

PHD2 继续作为 G3M2210M 与 guide-pulse 的唯一所有者。程序没有为旁星重新排名：

- 首次 PHD2 原生全画幅选择为 `(818.61,513.26)`，随后完成校准，正交误差约 `1.9°`。
- 一次 commissioning 计算错误把 N.I.N.A. `XYProjection` 与 PlateSolve3 原始方位角直接组合，导致约 `(-715,-182) px` 的错误预测。动作没有被解释成成功；程序沿同一 exact-lock ledger 完整返回 `(818.61,513.26)`，`62/62` 分段完成、0 丢帧、导星未中断，返回后 WCS 距原点约 `0.42″`。
- 原校准超过 30 分钟生产 TTL 后，PHD2 再次原生选中 `(835.41,496.08)` 并受控重标定：RA `-110.6° / 10.145 px/s`、Dec `-17.4° / 27.438 px/s`、正交误差 `3.2°`，完成于 `2026-08-24T12:42:42.415Z`。
- 在有界 waypoint，PHD2 原生恢复旁星 `(1108.47,170.01)`；没有由协调器替换另一颗星，后续保持 `Guiding`/settled。

关键本机证据：

- `output/commissioning/2026-08-24-night/evidence/phd2-deneb-native-recal-20260824T120533Z.json`
- `output/commissioning/2026-08-24-night/phd2-deneb-geometry-error-return-20260824T122528Z/`
- `output/commissioning/2026-08-24-night/evidence/phd2-deneb-native-recal-20260824T124038Z.json`
- `output/commissioning/2026-08-24-night/phd2-deneb-native-recover-at-waypoint-20260824T125238Z/`

## 3. PlateSolve3 传感器方向缺陷

实测 `+20 px` 探针和长位移证明当前 G3 的 plate scale 约 `0.383″/px`，PS3 sidecar 的 detector→sky Jacobian 使用约 `108°` 的旋转。N.I.N.A. 3.2 的 `Platesolve3Solver.ReadResult` 却把 PS3 输出的 `252.0836°` 原样写入 `PlateSolveResult.PositionAngle`，同时没有设置 `Flipped`。对 N.I.N.A. 的 `Coordinates.XYProjection` 而言，正确投影旋转应为：

```text
normalize(360° - 252.0836°) = 107.9164°
```

用 `g3-deneb-returned-20260824T123340Z_PS3.txt` 的不可变数值重放时：

| 路径 | 目录目标像素预测 |
|---|---:|
| 旧：直接使用 PS3 raw PA | `(1738.857, 599.981)`，错误侧 |
| 新：PlateSolve3 专用 complement | `(293.443, 947.331)`，实拍目标侧 |

源码新增 exact solver-identity 分支和回归测试；ASTAP 等已经由 N.I.N.A. 归一化的 solver 保持原语义。PS3 默认 `Flipped=false` 不再被文档冒充成测得的 parity；相机方向/parity 仍由版本化 commissioning preset 与 topology fingerprint 约束。

## 4. 从“缝端点”纠正为 fresh 几何中点

安全 waypoint 后，WCS 推断 Deneb 约为 `(610.24,473.19)`，黑色物理狭缝左端约为 `(612.60,434.02)`，距离约 `39.2 px`。第一次闭环只把目标送到 finite slit 的最近端点：目标饱和核约 `(609.35,432.66)`，而 fresh 黑孔径几何中点约 `(817.47,426.87)`。这证明“已经入缝”和“位于科学上更合适的中点”不是同一个门。

操作员指出狭缝中部进入光谱仪后的像差通常更小。随后通过同一 PHD2 current calibration、exact-lock、settle 和 fresh G3 复核把目标移至本轮实测中点：

- 入缝后稳态：黑缝中点 `(817.473,426.867)`，暗孔径角度约 `-2°`，对比度 `4.08σ`；目标饱和翼部质心 `(816.553,426.415)`，中点残差 `1.025 px`。
- 科学采集后：黑缝中点 `(817.473,426.867)`，对比度 `4.30σ`；目标翼部质心 `(818.921,426.280)`，中点残差 `1.562 px`；PHD2 仍为 `Guiding`。

由此接受 [ADR-0006](adr/0006-runtime-slit-midpoint-as-science-destination.md)：finite length 继续用于物理身份、反光排除、保护区与几何诊断，但 PHD2 和独立转换两条 fine-motion authority 的默认科学目的点统一为**本轮 fresh 黑孔径几何中点**。端点不再自动通过 completion gate。

关键本机证据：

- `output/commissioning/2026-08-24-night/phd2-deneb-finite-slit-safe-waypoint-20260824T124545Z/`
- `output/commissioning/2026-08-24-night/phd2-deneb-final-dark-slit-steady-20260824T125508Z/`
- `output/commissioning/2026-08-24-night/phd2-deneb-recenter-slit-midpoint-20260824T125628Z/`
- `output/commissioning/2026-08-24-night/phd2-deneb-slit-midpoint-steady-20260824T125916Z/`
- `output/commissioning/2026-08-24-night/phd2-deneb-post-science-slit-midpoint-20260824T130704Z/`

## 5. ATR 光谱与 QHY 测光闭环

ATR585M 由 N.I.N.A. 唯一拥有，按相机文档直接设定 `-10°C`，没有阶梯升降温。遥测由 `34.4°C` 开始下降，随后连续读到约 `-9.9/-10.0/-10.3°C`，制冷器开启且功率约 `69%`；到温判断使用连续实际温度/实际 set point，不使用严格相等派生的 `AtTargetTemp`。

N.I.N.A. 原生拍摄完成 `10 s LIGHT ×3`、gain 100。旧质量门当夜把三张都标成通过，但操作员复核二维谱后指出迹线明显削顶；随后按不可变 FITS 回放证明这是**质量门误放行**，三张均只能保留为过曝技术证据，不能作为合格科学帧。旧门按整幅 3840×2160 ROI 统计饱和像素，窄迹线即使在数百至上千个波长列削顶，整幅比例仍可能低于 `0.1%`：

| 本地时间文件 | SHA-256 | 旧整幅饱和比例 | 新迹线局部饱和 | 削顶波长列 | 最长连续削顶 | 结论 |
|---|---|---:|---:|---:|---:|---|
| `2026-08-24_21-02-48__-10.40_10.00s_0000.fits` | `AED2F6DD…B0F98` | `0.0326%` | `1.435%` | `906/3840 = 23.59%` | `537` | 拒绝，过曝技术帧 |
| `2026-08-24_21-05-17__-10.00_10.00s_0000.fits` | `7E83589F…6EB98D` | `0.0812%` | `3.580%` | `1355/3840 = 35.29%` | `1195` | 拒绝，过曝技术帧 |
| `2026-08-24_21-05-28__-10.00_10.00s_0000.fits` | `AB9BB185…C09A29` | `0.0910%` | `4.009%` | `1623/3840 = 42.27%` | `1586` | 拒绝，过曝技术帧 |

源码 `0.4.0.11` 改为先在配置的空间提取孔径内自动找谱迹，再分别记录迹线局部饱和比例、任意迹线像素削顶的波长列比例和最长连续削顶列。默认梯度包含 `0.01/0.03/0.1/0.3/1/3/10/15/30… s`，初始探针为 `0.1 s`；具体梯度和初始档仍是用户可见配置，不是按目标名硬编码。选档结果与当前探针不同时，必须在新档位再拍一张 fresh 探针并重新通过同一门，不能用已经削顶的帧线性外推后直接开始科学曝光。科学块每张候选帧复用相同硬门，失败帧不可变保留并触发重新探针。

QHY service 同时在当时标为 physical R/slot 5、经 2026-08-26 校正确认为实际 Sloan i′/slot 5 的轮位完成 run
`deneb-slit-midpoint-20260824T130121Z`：`5 s ×4`、gain 20、offset 20、`-10°C`，4/4 接受。manifest 位于：

`C:\ProgramData\UVEX-ADV\qhy\data\runs\deneb-slit-midpoint-20260824T130121Z\qhy-01d1c05016064f35bd5b30869c558a50\manifest.json`

UVEX4 在采集时为 COM5 `Ready`、狭缝位 2 / 15 µm、grating `-1923` / `5591.5 Å`、M2 `12500`、照明 LED `Off`。采集后 fresh G3 中点复核通过，证明这不是只在曝光前短暂入缝。

## 6. 自动化边界的最终判定

### Deneb commissioning 阶段

Deneb 的物理闭环有人工/大模型监督。PlateSolve3 raw PA 造成错误投影后，路线判断、完整回程和安全 waypoint 由 commissioning 过程监督；“已入有限缝端”改成 fresh 中点，以及旧 ATR 整幅饱和指标误放行，也由操作员视觉反馈触发。因此 Deneb 只证明真实物理链和故障恢复，不被回写成无人值守成功。

### Mirfak 与 Algol 重放阶段

之后的两个 run 由同一个普通 PowerShell/.NET 确定性 field harness 编排成熟 owner functions；从单次启动到完成没有人或大模型选择方向、目标像素、导星星、狭缝位置、曝光档或追加修正。两个 manifest 都记录：

- `OperatorSafetyAttestation=OPERATOR-ATTESTS-ROOF-OPEN-CLEAR-SKY-DEVICE-MOTION-AUTHORIZED`；
- `NoManualOrModelCorrectionAfterSingleStart=true`；
- `FullRoofWeatherAutomationClaimed=false`。

因此可以准确声称：**核心入缝—导星—ATR/QHY 配对拍摄流程已完成两次真实无人干预闭环。** 这不等于整个天文台无人值守，因为屋顶、天气和安全联锁仍由现场操作员承担；也不等于 N.I.N.A. 插件主页的 production runner 已完成同级验收，field harness 的确定性路径仍需全部并回并安装重放。

## 7. Mirfak 首次无人干预核心闭环

证据目录：

`output/commissioning/2026-08-24-night/mirfak-unattended-20260824T193125Z/`

本地时间 `03:31:25–03:37:15`，manifest `Succeeded=true`。流程由 N.I.N.A. 目录转向开始，QHY 旧标 R/实际 i′、5 s 固定轴见证后，G3 每个位置使用两张独立 10 s 解算。大残差由 N.I.N.A. 直接移动，不用 PHD2 蠕动；目标—狭缝粗残差约由 `3608.16 px → 81.27 px → 23.99 px`，对应各步 fresh N.I.N.A. 响应残差约 `30.96″、8.94″、9.17″`。

最终程序使用 PHD2 `find_star` 返回的原生旁星，不自己排序替换；目标测量进入 `SlitObscuredSaturatedCore`，fresh 黑缝中点的目标峰残差 `2.708 px`、exact-lock 残差 `0.0051 px`，PHD2 保持 guiding。ATR 自动访问 `0.01 → 0.03 → 3 s`，以 fresh 3 s 探针验证后接受 `3×3 s`；同时取得 3 张配对 QHY 帧，ATR 迹线削顶列/局部饱和均为 0。

## 8. Algol 第二目标无人干预复现

证据目录：

`output/commissioning/2026-08-24-night/algol-unattended-20260824T193919Z/`

本地时间 `03:39:19–03:45:51`，manifest `Succeeded=true`。这是换目标后的独立全流程，不复用 Mirfak 的最终目标坐标或导星点。粗残差约由 `3117.47 px → 60.24 px → 15.44 px`；最后一次 N.I.N.A. 直接移动的 fresh 响应残差约 `2.663″`。PHD2 原生选星后一次 exact-lock 把目录目标送至 fresh 中点：最终稳健目标残差 `0.399 px`、最后帧约 `0.537 px`、锁点残差 `0.00488 px`，五个 fresh 验证样本均通过。

ATR 自动访问 `0.01 → 0.03 → 3 → 10 s`，不是按 Algol 名称或固定 10 s 拍摄；10 s 档自己的 fresh 探针通过后才开始科学块。完成 3 张 ATR 与 3 张配对 QHY，结束时 PHD2 仍在 guiding。

## 9. Algol 三张 10 s 科学帧重新审计

操作员随后要求确认“最后三张是否真的不过曝”。重新从原始 FITS 读取并核对 manifest SHA-256 后得到：

| 文件本地时间 | 迹线中心 y | 迹线 p99.9 / max ADU | 扣偏置满量程利用率 | 迹线削顶 |
|---|---:|---:|---:|---:|
| `03-45-16` | `719` | `13725 / 15216` | `22.9%` | `0` |
| `03-45-28` | `722` | `15536 / 16944` | `25.6%` | `0` |
| `03-45-40` | `723` | `28032 / 30192` | `45.9%` | `0` |

三张都没有迹线像素达到 50% 满量程，更没有削顶列或连续削顶段，结论是**没有过曝**。整幅图每张只有同一个 `(3329,1124)` 像素为 `65520 ADU`，位于迹线之外，三帧位置固定；这解释了约 `1/8294400` 的整幅饱和比例，应作为待 commissioning 的固定坏/热像素候选，而不是光谱过曝。

但曝光不等于最优：前两帧偏保守，第三帧通量高约 61–66%，谱形也随狭缝耦合/视宁度改变。当前 science block 冻结已验证档位，只逐帧硬拒绝削顶，没有滚动上/下调。默认梯度新增 15 s，下一增量采用多探针上包络与有界逐帧重探测；详见[暗目标与稀疏场设计](design/faint-target-and-sparse-field-acquisition.md)。诊断位于 Git 忽略的 `tmp/algol-science-review/`。

## 10. HD 19445 暗目标/稀疏场挑战

为避免继续只优化亮星，最后约半小时换到 HD 19445（V≈8.06）。目录 J2000 坐标按其高自行传播到 2026-08-24，使用 RA `47.1048860631°`、Dec `26.3248033839°`；否则原历元坐标会带来约 22.5″ 误差。

自动 run 证据目录：

`output/commissioning/2026-08-24-night/hd19445-v8-06-epoch2026-unattended-20260824T201846Z/`

N.I.N.A. 正常转向，QHY 旧标 R/实际 i′、5 s 见证帧检测 32 星、最大值 `2555 ADU`、无饱和。随后：

| G3 位置/曝光 | 提取源 | 目录匹配 | PlateSolve3 |
|---|---:|---:|---|
| 原目标场 10 s ×3 | 首帧最高 `79` | `0` | 全部 `False` |
| 原目标场 30 s | `68` | `0` | `False` |
| 东邻场 5′，30 s 首帧 | `22` | `7` | `True`，低匹配候选 |
| 同一东邻场再拍两帧 | `12 / 4` | `0 / 0` | `False / False` |
| 北邻场 5′，30 s | `85` | `0` | `False` |

程序的初始 run 在没有两份可信一致 WCS 时于任何后续移动/入缝前 fail-closed。之后的邻场动作是显式 commissioning；单份 7-match 解没有被拿来授权科学目标移动。东邻场首帧到后续帧在几分钟内快速劣化，北邻场虽提取到 85 个亮结构却零匹配，发生时间又最接近天亮。故本轮准确结论是：**晨光使可用星形和目录匹配快速失效，半小时窗口耗尽；不把挑战失败归因成程序。**

同时得到三条程序结论：

1. 长曝光本身不能保证解算，源数也不能代替正确匹配；解析器确实可能返回错误解或低匹配歧义解，所以不能删除尺度、方向、位置提示和多帧一致性门。
2. 5′ 邻场首帧能正式解算，验证了“少量移动—长曝光—邻场解算”的退化方向；但单解与简单固定光轴差假设相差约 `304.9″`，不能硬编码光轴差后直接运动。
3. 当前 field harness 中 `sources≥20 / matches≥5` 等数值是临时 commissioning 验证逻辑，不是 production 全目标通用门；production 应改为强解对、低匹配时间簇、重叠多场图和可信双镜同步映射等分层证据。

## 11. 已固化到源码的行为

1. `NinaNativeQhyCaptureSolver` 将 QHY service 的不可变帧交给 N.I.N.A. 原生 `CenteringSolver`；`NinaNativeCenteringTelescopeProxy` 只在 N.I.N.A. 请求运动时执行既有独立粗居中安全门和分段。
2. `G3WcsTargetProjector` 对 exact PlateSolve3 identity 使用 complemented rotation；真实 Deneb sidecar 成为测试 oracle。
3. PHD2 与独立 `G3PixelToMount` 后备的完成 residual 和修正向量统一指向 fresh 黑孔径几何中点，不再以 finite endpoint 为成功。
4. 选旁星只调用 PHD2 原生 `find_star`，程序只验证其返回点的 halo、饱和、SNR、边缘、暗缝和形态；验证失败不自己重排替换。
5. N.I.N.A. 启动前若 OnStep UTC 陈旧，runner 只允许一次 exact-owner 断开/重连同步，并在继续前验证时钟、身份、pier side、位置和跟踪；不通过则不运动。
6. `SpectralTraceQualityAnalyzer` 在空间提取孔径中确定谱迹，按波长列检查削顶；每次改变曝光档都必须 fresh 重拍并通过。默认配置补入 10/30 s 之间的 15 s，但完整梯度仍可修改、可见、参与配置哈希。
7. commissioning preset 升为 schema 5，绑定 fresh 狭缝中点、PHD2 placement 和新增质量策略；界面校验文字同步为 schema 5。
8. 暗目标/邻场方案固化为[独立设计文档](design/faint-target-and-sparse-field-acquisition.md)，没有把尚未实现的低匹配图或多场拼接冒充为已上线功能。

## 12. 仍需完成的生产条件

- 把本夜 deterministic field harness 已验证的完整顺序全部并入并安装 N.I.N.A. 插件主页 runner，再做一次从按钮开始、无外部脚本决策的重放。
- 实现 `AutoPreferDirectTargetThenOffSlit` 在 direct target 入缝消失后的 durable guide-epoch handoff；当前 production source 在中途丢失时仍安全暂停，见 `OBS-010`。
- 实现低匹配多帧候选簇、带重叠率的邻场图、回原点和 `InvisibleInG3` 的 WCS+旁星+ATR 信号终验，见 `OBS-014`。
- 实现 ATR 多探针上包络和 science block 滚动曝光；暗目标以累计 stack SNR/总积分计划为主，不套用亮星每帧 SNR 门，见 `OBS-015`。
- 当前没有可信接入的 roof/dome、Safety Monitor 和本地天气 authority；所以可声称的是核心光谱闭环无人干预，不是整座台站无人值守。
- 尚需多 pier side、多狭缝、过中天、薄云恢复、3C 273/行星状星云和长时间吞吐验收。

## 13. 观测结束设备归位

约 `04:53–04:58 CST` 在不操作屋顶的前提下完成：

- PHD2 已停止，随后 `get_app_state=Stopped`、G3M2210M 和 PHD2 OnStep 均 `connected=false`；
- QHY service 最近 50 个 job 全部为 `Completed`，QHYminiCam8M 正常断开，温度/制冷功率终态为空且无错误；
- ATR585M 无曝光，按官方手册策略不做阶梯升温；由 N.I.N.A. 正常断开后 `Connected=false、IsExposing=false、CoolerOn=false、CoolerPower=0`；
- Gemini 平场板先关闭照明，再等待 `CoverState=Closed`，随后才允许赤道仪停驻；
- OnStep 停驻前后 fresh 回读为 `AtPark=true、TrackingEnabled=false、Slewing=false、IsPulseGuiding=false`，之后由 N.I.N.A. 断开；
- UVEX4 不移动狭缝、光栅或 M2；COM5 fresh `SLOF` 成功并回读 `Ready / positionTrust=Live / slit LED=Off`。终态仍为 slit 2 / 15 µm、grating `-1923` / `5591.5 Å`、M2 `12500`；
- 屋顶明确留给操作员关闭，本文不声称软件关闭或验证屋顶。

完整终态清单及 UVEX LED 关灯的不可变本机证据位于：

- `output/commissioning/2026-08-24-night/closeout-20260824T205703Z/closeout-manifest.json`（SHA-256 `DB288EC81803B78449FC165624D9FF72D6A9C5A1820ED6263EED12238D79CB26`）
- `output/commissioning/2026-08-24-night/closeout-20260824T205703Z/uvex-led-off/`
