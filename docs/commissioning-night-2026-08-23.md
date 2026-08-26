# 2026-08-23/24 Deneb 与 Mirfak 自动观测闭环实机调试

**本地观测夜：** 2026-08-23（Asia/Shanghai）  
**现场条件：** 大丰近海台址，间歇性快速浅薄碎云；屋顶由操作员手动开启，平台周边约 40° 遮挡  
**结论：** “N.I.N.A. 指向/居中 → QHY/G3 各自 fresh WCS → PHD2 原生选星/校准 → 黑色有限狭缝分段入缝 → 稳定导星 → N.I.N.A. ATR 10 s 光谱 + QHY 4×5 s 同时测光”技术闭环已走通；天亮前又从停放态快速复现到 `0.388 arcsec` 的 fresh WCS 入缝残差。本轮仍不是科学曝光或无人值守验收。

> **后续设计说明（2026-08-24）：** 本文保留当夜“有限缝段最近点”的历史结论，不回写旧证据。后续 Deneb 实测与光谱像差考虑已由 [ADR-0006](adr/0006-runtime-slit-midpoint-as-science-destination.md) 将默认科学目的点改为**本轮 fresh 黑孔径几何中点**；端点只保留为物理几何、保护区和诊断，不再作为自动入缝完成点。

本记录由当夜不可变 FITS、PHD2 事件审计、N.I.N.A. 图像历史和最终只读设备状态重建。原始观测仍位于 Git 忽略的 `output/commissioning/2026-08-23-night/evidence/` 和 N.I.N.A. 图像目录，没有被改名、移动或重写。本文只固化可重复的软件结论；单夜像素位置、两镜光轴差和现场放宽的 commissioning 门均不得写成机器无关常量。

## 1. 同一观测夜中的阶段性收口

2026-08-23 23:17 CST 曾进行一次只读阶段性收口，而不是把“停止测试”误写成“设备已归位”。操作员随后在**同一个观测夜**确认云隙恢复并重新开顶，继续完成了 Mirfak 闭环；因此下表只描述当时的中间状态，不是本文末尾或当前设备状态：

| 设备 | 唯一所有者 | 最终实报 |
|---|---|---|
| OnStep 赤道仪 | N.I.N.A. | `AtHome=true`、`Slewing=false`、跟踪关闭；高度约 `33.376°`、方位约 `0.193°`。驱动 Home 语义下 `AtPark=false`，因此本轮登记为 **Home**，不伪称 Park。 |
| Gemini 平场板/镜盖 | N.I.N.A. | 已连接，`CoverState=Closed`、`LightOn=false`、亮度 0。 |
| ATR585M | N.I.N.A. | 无曝光、制冷关闭、已断开。断开前执行过一次有界升温请求，但高级 API 温度仍卡在 `0 °C`，不能据此证明真实传感器温度。 |
| QHYminiCam8M | QHY service | `connected=false`，无活动 job。断开后实体滤轮位置不可实报，程序没有猜测其位置。 |
| G3M2210M | PHD2 | PHD2 进程仍可供下次使用，但 event server 实报 `Stopped`；没有导星或曝光循环。 |
| UVEX4 | `UvexAdv.Service` / COM5 | `Ready`，狭缝轮位 2，狭缝照明 LED `Off`，无错误；没有在收口时移动 M2、光栅或狭缝轮。 |
| 屋顶 | 现场既有人工系统 | N.I.N.A. dome/roof 未连接、`CanSetShutter=false`，软件无法关闭或验证屋顶。屋顶状态必须由操作员使用原控制链确认；不得把本轮软件状态当作屋顶已关闭证明。 |

## 2. 本夜实际证明的闭环

### 2.1 启动条件与 QHY 粗居中

- 赤道仪 UTC 硬门通过，实测主机/驱动时差约 1 秒量级。
- Deneb 目录目标 J2000 为 `310.3579791667°,+45.2803388889°`。
- `qhy-deneb-recenter-20260823T140555Z` 在薄云下降低星数门的显式 commissioning 条件下成功：两次 N.I.N.A. 赤道仪动作、累计实测约 `610.568 arcsec`，最终 WCS 残差 `7.547 arcsec`，最后一帧检测 144 星、FWHM 约 `3.41 px`。
- 该路径没有使用 QHY→G3 固定偏移，粗运动门与 G3/狭缝精修门保持分离。

### 2.2 两镜快速同指向解算只生成 Candidate

当 G3 获得 WCS 后立即补拍/解析 QHY，证明了用户提出的快速双解算路线可工作：

- 第一对中心约为 G3 `309.948964°,+45.500941°`、QHY `309.998658°,+45.571914°`，推导的 pierWest 单样本约为东 `+126 arcsec`、北 `+255.5 arcsec`、总量约 `285 arcsec`。
- 约一小时后的快速对中心约为 G3 `310.072680°,+45.497705°`、QHY `310.120214°,+45.556110°`，对应约东 `+120 arcsec`、北 `+210 arcsec`、总量约 `242 arcsec`。
- 两次候选在赤纬分量已有约 45 arcsec 差异，且两次曝光/解算并非严格同时。因此它们只证明 `MotionAuthority=false` 的版本化 Candidate 采集链，反而再次证明不能把某一次两镜差写成源码常量。激活预置模型仍需同安装纪元、两 pier side、温度/高度范围和多样本残差验证。

### 2.3 亮目标选星失败、临时 ROI 修复与最终生产结论

全画幅亮度优先的 PHD2 自动选星曾锁到 Deneb 的饱和光晕岛，操作员截图和 PHD2 星像剖面均显示它不是独立星点。PHD2 多星圆圈也不能证明这些峰属于真实独立恒星。这次失败不能通过放大容差或继续 settle 合法化。

同一张 2 s 级 G3 帧随后给出了可用的独立星点。当时为抢救云隙采用了“协调器选候选 + PHD2 局部 ROI”的临时路线，并完成了后续 Deneb 技术闭环。它是有效的现场恢复证据，但不是最终生产架构，因为它重复实现了 PHD2 已有的自动选星功能。

最终生产规则是：

1. PHD2 在自己拥有的 G3 全幅中执行原生 `find_star`；
2. 协调器只验证 PHD2 返回的同一个点是否在 fresh G3 候选中匹配紧致恒星，并检查 SNR、FWHM、椭率、饱和、边缘、超亮目标光晕和物理狭缝保护区；
3. 验证失败就否决该选择，不排序、也不替换成协调器认为更好的另一颗星；
4. 亮目标可见时优先使用单独 commissioning 的最短曝光直接导目标，目标不适合直接导星时才进入 PHD2 原生旁星路线；
5. 10 ms 是有效的 PHD2 曝光档位；切换曝光后丢弃管线中的首张旧曝光帧，并验证保存 FITS 的实际曝光，避免把残留 50 ms 帧的光晕当作 10 ms 结果。

短曝直导不是对焦证据。无旁星的极端场景仍允许有人监督地顶着大气跳动和目标在狭缝上下漂移继续导目标，不得因此无限停住。

### 2.4 第一次真实 ROI 入缝闭环

证据：`phd2-deneb-roi-closed-loop-20260823T141202Z/`。

- 同帧 WCS 目标质心 `(796.946,974.721)`，狭缝采集点 `(937,440)`。
- 形态合格的独立导星候选 `(1298.884,677.789)`：SNR 约 `19.25`、FWHM 约 `4.84 px`、椭率约 `0.329`；选择 ROI 为 `80×80 px`。
- 使用 `desiredGuideLock = guide + (slit - target)`，目标 lock 为 `(1438.938,143.068)`。
- PHD2 完成 46 个、每个不超过 12 px 的 exact-lock 分段；每段都产生本地 operation-bound settle 和 fresh G3 帧。最终 lock `(1438.94,143.07)`，lock 几何残差约 `0.0028 px`。
- 独立最终 G3 WCS 把 Deneb 投影到约 `(932.006,440.627)`，相对采集点残差约 `5.033 px`，按当晚 `0.3805 arcsec/px` 约为 `1.92 arcsec`。这是本夜最强的 fresh 目标入缝验证。
- audit 中基于“最亮峰”的旧降级读数仍给出约 40.6 px，这是 Deneb 光晕/鬼影污染的反例，不能覆盖独立 WCS 的目标身份结论，也正是生产代码必须停止 brightness-only 选星的原因。

### 2.5 薄云丢锁后的重捕获与 ATR 技术曝光

证据：`phd2-deneb-reacquire-roi60-20260823T143650Z/`（目录名保留当时工具试验名；实际审计 ROI 为 `80×80 px`）。

- fresh WCS 预测目标 `(1354.044,225.829)`；在相距约 `393 px` 处选中独立星 `(914.064,353.847)`，SNR 约 `13.57`、FWHM 约 `4.11 px`、椭率约 `0.173`。
- PHD2 接受 `(920.45,355.14)`，后备搜索 ROI 为 `(880,315,80,80)`，没有再次落回 Deneb 光晕。
- 40 个有界 exact-lock 分段后最终 lock 为 `(497.02,568.02)`，几何读回残差约 `0.0014 px`。
- 碎云期间出现 3 个瞬时 `StarLost`，但同一个本地 settle operation 后续恢复 `GuideStep` 并最终 `SettleDone`；没有星点切换。由此确认 `StarLost` 不应立即销毁仍在进行、最后由 PHD2 明确裁决的 settle epoch。
- 第二张 ATR 技术曝光由 N.I.N.A. 唯一所有者路径完成：`2026-08-23_22-44-55__0.00_10.00s_0000.fits`，10 s、SHA-256 `BD27C30C9F220B8ED3854C4663F862EB52B482B21B5052DB1718BCCEC6F33DAA`。曝光前 settle 为 2 帧、0 丢帧；完整曝光期间 0 个 guide-loss 事件；曝光后仍为 `Guiding`，N.I.N.A. 记录 RMS `1.95 px / 0.75 arcsec`。
- ATR 帧存在清晰水平光谱迹线，证明“入缝—导星—N.I.N.A. 曝光”的技术路径已经形成。但 ATR API 同时报告实际 `0 °C`、目标 `-10 °C`、制冷功率约 26%，温度语义不自洽；因此该帧分类固定为 `TechnicalClosedLoopEvidenceNotScience`。

### 2.6 Mirfak 黑色有限狭缝终验与 ATR/QHY 同步采集

操作员在同一观测夜重新开顶后，Mirfak 路线纠正了 Deneb 阶段仍未暴露的两个几何错误：固定点 `(937,440)` 不是 2 号缝的唯一物理目标；亮峰、光晕和上方强反光也不能代表真缝或目标星核。历史 HDR 黑孔径证据给出的 2 号缝中心约为 `(817.299,421.870)`、长度约 `410 px`、宽度约 `3.5 px`、经验不确定度约 `1.279 px`。因此最终判据改成目标到**有限黑色孔径中心线最近点**的距离，只消除法向误差，不追逐反光，也不把合法的沿缝位置拖回中点。

实机步骤和最终证据：

- PHD2 原生全画幅选择旁星并保留唯一 G3/导星脉冲所有权；新 guide epoch 后使用 fresh G3 WCS、原生旁星和黑缝几何重新规划，未复用丢锁前坐标。
- `phd2-mirfak-dark-slit-final-20260823T191435Z` 完成 34 个不超过 12 px 的 exact-lock 分段，34 次 settle 全部成功、0 丢帧，最终 lock 为 `(1539.02,268.28)`。
- PHD2 `get_pixel_scale` 的精确读回为 `0.383749 arcsec/px`，不是界面四舍五入后的 `0.4`。按精确比例发现仍有约 `5.62 px` 法向残差，随后只作一次法向微调：`phd2-mirfak-dark-slit-normal-correction-20260823T192354Z`，最终 lock `(1539.22,273.90)`，几何读回残差约 `0.00485 px`，仍为 `Guiding`。
- 修正后新鲜 G3 帧 `g3-mirfak-dark-slit-normal-correction-20260823T192408Z.fits` 的 PlateSolve3 中心为 RA `51.1069981484°`、Dec `49.8473508514°`，N.I.N.A. PA `108.5203336°`。黑缝重新检出于约 `(817.665,428.861)`、角度 `-3°`、对比度 `3.96σ`；目录目标沿缝约偏 `32 px`，仍在约 `205 px` 半长内；法向残差按精确 PHD2 比例约 `0.03 px`，按独立解算比例约 `0.46 px`，均小于约 `1.75 px` 半缝宽和既有标定不确定度。
- 操作员提供的“不精准”截图属于这次法向微调之前的证据；微调后的上述新鲜帧才是最终验收帧。不得再根据旧截图重复纠偏。
- 同一运行 `mirfak-closed-loop-20260823T192822Z` 中，QHYminiCam8M 在当时标为 R、实际为 Sloan i′ 的轮位完成 `4×5 s` 测光，4/4 帧接受、每帧检测 `167–173` 星、后三帧透明度约 `0.992–0.995`、无质量标志；manifest SHA-256 为 `B66946ADFCFC2E4FBB3907339A5BF95CCE685E6F3BC120C9DD276BD6FD956112`。
- N.I.N.A. 唯一所有者路径同时完成 ATR585M `10 s` LIGHT：`2026-08-24_03-28-23__0.00_10.00s_0000.fits`，SHA-256 `6C761C153183B32818FCDC59D138A551A16165A369F37B717A88F3D0842C5189`。连续谱贯穿全幅，迹线检测 24/24 分箱有效、中位中心约 `y=760.58 px`、中位 FWHM 约 `29.13 px`。QHY 与 ATR 曝光重叠约 10 秒。
- 拍摄后 fresh PHD2 帧 `post-capture-guiding-20260823T192850Z.fit` 保留，lock 前后均为 `(1539.22,273.90)`，PHD2 未中断。由此完成“最新精确入缝 → 持续导星 → ATR 光谱与 QHY 光度并行拍摄”的一次真实设备技术闭环。

## 3. PHD2 事件和校准的新结论

- 本夜 active calibration 为 RA 方向约 `-101.3° / 16.827 px/s`、Dec 方向约 `-11.7° / 24.563 px/s`；两轴正交误差实际约 `0.4°`。这里的 `-11.7°` 是 Dec 轴在传感器上的方向角，不是“11.7° 正交误差”。
- PHD2 在真实接管中可能按不同顺序发送 `LockPositionSet`、`StarSelected`、`StartGuiding`、`SettleBegin` 和 `LoopingExposuresStopped`。如果重复位置仍在刚刚由本地程序、同帧形态门接受的候选邻域，它是接管确认，不是外部换星。
- `GuideStep` 可在一次薄云 `StarLost` 后恢复 Guiding；当前 settle 的最终权威是同一个本地 operation 对应的 `SettleDone`，而不是任何单一中间事件。
- 当前校准只有“一个 active calibration 被评估”，没有历史库择优。0.4° 本夜证据也不能追认旧安装纪元或另一 pier side。

## 4. 已落实到生产源码的改动

1. `IPhd2Client/Phd2Client` 重新公开 PHD2 原生全画幅 `find_star`。旁星路线以原生结果为唯一候选，协调器只验证该点是否匹配 fresh G3 中的紧致恒星以及 SNR、光晕、边缘、饱和和狭缝保护门；验证失败不自行排序替换。
2. 新增 `AutoPreferDirectTargetThenOffSlit`：亮目标可见时先用单独 commissioning 的最短曝光直导目标；不可用时才进入 PHD2 原生旁星路线。无独立星仍保留有人监督的短曝直导退化，不把它当对焦证据。
3. PHD2 曝光切换若改变档位，会等待两张新的 loop 帧并丢弃首张相机管线旧帧，再保存/核对第二张 FITS；这修复了请求 10 ms 却保存上一张 50 ms 光晕帧的错误。10 ms 本身是当前 PHD2/Profile 的合法档位。
4. PHD2 客户端读取 `get_pixel_scale` 精确值，运动换算不再依赖 UI 四舍五入显示。
5. `GuideStarSelector.ClosestPointOnSlit` 和全部入缝 residual/修正统一使用有限黑缝中心线最近点；只消除法向误差，越过物理端点时才夹到端点。反光边、亮峰和历史中点不再是唯一运动目标。
6. PHD2 事件状态机继续保留同 operation 的瞬时丢星 settle，并允许后续 `GuideStep` 恢复；fresh residual 仍是 lock readback 之外的必需物理证据。
7. 设备收口后完整 release build 已通过，`0 warning / 0 error`；全部 .NET 测试通过（含 Observatory `215/215`、PHD2 `99/99`、N.I.N.A. plugin `186/186`、QHY `27/27`、commissioning tool `15/15` 等），Python reduction `ruff` 通过、`pytest 62/62`。第一次并行全套运行曾在 QHY 测试读取临时 manifest 时遇到一次瞬时文件占用；精确用例、QHY 全套及最终完整 build 均通过。新 artifact 尚未安装，不得把源码/构建测试冒充已部署实机回归。
8. Mirfak 现场工具已证明“目标消失/丢锁后由 PHD2 原生旁星接力 + fresh WCS 验证仍在缝上”可行；当前 production `AutoPreferDirectTargetThenOffSlit` 只实现初次直导选择失败时的旁星回退。exact-lock 中途丢失直导目标后的跨 guide-epoch durable handoff 尚未完成，已登记为 `OBS-010`，不得在摘要中误写成全自动已部署。
9. ATR 温度门只使用 exact N.I.N.A.-owned 相机的实际温度、实际 set point、`CoolerOn` 和功率/趋势；Advanced API 的 `AtTargetTemp` 是严格浮点相等派生值，只写审计，不作为独立硬门。2026-08-24 04:35 CST 的并行块预检在 `-9.9 °C / -10.0 °C` 时因临时脚本误用该布尔值而拒绝，且拒绝发生在 QHY job 和 ATR 曝光创建之前。生产源码没有把该布尔值作为温度门。
10. ATR585M 官方手册把温控描述为 PID 直接调节到目标温度，并未要求阶梯升降温。默认程序策略固定为直接设定目标并等待连续真实遥测稳定；收口只需确认无曝光、关闭 TEC、正常断开。升降温斜坡只能作为显式、可配置的机型/现场策略，不能成为 ATR585M 的隐含默认值。

## 5. 尚未通过、不得扩大的结论

- Deneb 第二轮重捕获后的独立最终 G3 WCS 被云破坏，不能把其亮峰残差当作 fresh 目标入缝验收；该限制不覆盖后来 Mirfak 的新鲜板解/黑缝法向终验。
- 本夜较早阶段的 ATR 温度实报不可信；天亮前重连后获得了从 `-9.9 °C` 到环境温度的连贯遥测，但没有在这一新温度证据下完成第二个 ATR/QHY 并行块。因此本夜仍没有科学帧、科学 SNR、波长覆盖、校准灯或多小时稳定性验收。
- Mirfak 已完成 ATR/QHY 同时技术采集，但它仍是独立现场工具运行，不是安装后的 production runner 全自动一键执行，也没有科学温度权威；不得升级成无人值守或正式科学验收。
- 屋顶、安全监视器和天气仍无软件权威；本轮只能登记为有人监督的技术闭环。
- 两镜快速配对的时间间隔仍有数十秒，下一步应以 runner 内 G3 WCS 成功事件直接触发 QHY 缓存复用或一次短曝光，记录曝光中点间隔，而不是用文件写入时间冒充同步。
- 80 px 是本夜经实机验证的保守“后备搜索围栏”，不是星点物理尺寸或两镜标定。未来如需适配不同 G3 ROI/binning，应把它纳入版本化 commissioning preset；不得用它放宽 PHD2 接受位置或跨越整幅光晕。

## 6. 下一晴夜最短验收

1. 完整构建并安装包含原生 PHD2 选星、精确 pixel scale、10 ms 管线刷新和有限黑缝法向判据的插件；重启 N.I.N.A. 只能在屋顶关闭且操作员明确授权后进行。
2. 选取高于现场 40° 遮挡并覆盖完整最坏耗时的亮目标，由 N.I.N.A. 完成目录指向和 QHY 粗 WCS 居中。
3. G3 WCS 一成功立即触发 QHY 快速配对，记录两曝光中点间隔；Candidate 只入库，不自动授权两镜预置。
4. 检查 evidence 中 PHD2 原生返回点及其 FWHM/椭率/SNR/光晕距离；协调器不得显示或使用另一个替代排名。随后直接运行一轮法向入缝、settle、fresh G3 residual。
5. 云中瞬时丢星交由同 operation settle 裁决；`SettleDone` 失败、ROI 内无星或 fresh residual 失败才进入重捕获/退化路线。
6. ATR 直接设定到目标温度；实际温度、实际 set point、`CoolerOn` 和功率/趋势连续一致且到温后，再重复一张由 N.I.N.A. 保存的技术帧。不得等待 `AtTargetTemp` 的严格相等布尔值，也不得默认执行阶梯升降温；之后才开始 QHY 测光与 ATR 科学块共同运行。

## 7. 关键证据索引

- `output/commissioning/2026-08-23-night/evidence/qhy-deneb-recenter-20260823T140555Z/manifest.json`
- `output/commissioning/2026-08-23-night/evidence/g3-DenebSolve/`
- `output/commissioning/2026-08-23-night/evidence/qhy-DenebPair/`
- `output/commissioning/2026-08-23-night/evidence/g3-DenebRecent/`
- `output/commissioning/2026-08-23-night/evidence/qhy-DenebFastPair/`
- `output/commissioning/2026-08-23-night/evidence/g3-Deneb/g3-deneb-roi-final-20260823T141202Z-selection.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-roi-closed-loop-20260823T141202Z/audit.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-reacquire-roi60-20260823T143650Z/audit.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-reacquire-roi60-20260823T143650Z/atr-guided-technical-capture-repeat.json`
- `output/commissioning/2026-08-23-night/phd2-mirfak-dark-slit-final-20260823T191435Z/`
- `output/commissioning/2026-08-23-night/phd2-mirfak-dark-slit-normal-correction-20260823T192354Z/`
- `output/commissioning/2026-08-23-night/evidence/g3-Mirfak/g3-mirfak-dark-slit-normal-correction-20260823T192408Z.fits`
- `output/commissioning/2026-08-23-night/evidence/atr-Mirfak-closed-loop-20260823T192823Z.png`
- `output/commissioning/2026-08-23-night/evidence/g3-Mirfak/post-capture-guiding-20260823T192850Z.fit`
- `C:\ProgramData\UVEX-ADV\qhy\data\runs\mirfak-closed-loop-20260823T192822Z\qhy-380a95ae41284a18951e793f78b53c17\manifest.json`（本机忽略的实机数据；不进入 Git）
- `output/commissioning/2026-08-23-night/qhy-mirfak-reopen-20260823T201739Z/`
- `output/commissioning/2026-08-23-night/evidence/g3-Mirfak-reopen-20260823T202139Z/`
- `output/commissioning/2026-08-23-night/nina-mirfak-g3-reopen-center-20260823T202305Z/`
- `output/commissioning/2026-08-23-night/phd2-mirfak-reopen-recal-20260823T202610Z/`
- `output/commissioning/2026-08-23-night/phd2-mirfak-reopen-slit-20260823T202956Z/`
- `output/commissioning/2026-08-23-night/phd2-mirfak-reopen-normal-20260823T203154Z/`

## 8. 收口后部署验收

操作员确认屋顶已关闭并明确授权重启后，于 2026-08-23 23:59 CST 完成部署：

- N.I.N.A. 通过正常窗口关闭流程退出，没有强制终止；
- 原子安装 `OpenAstroSpec Auto — UVEX4` `0.4.0.8`，安装目录中 7 个必需 DLL 的 SHA-256 与本轮完整构建 artifact 逐一一致；
- 新 N.I.N.A. 进程正常响应，Advanced API `2.2.15.2` 可用，插件目录清单包含 `UVEX-ADV Spectroscopy`；
- 新进程日志明确记录 `Successfully loaded plugin OpenAstroSpec Auto — UVEX4 version 0.4.0.8 by OpenAstroSpec`；
- 新日志没有 OpenAstroSpec、XAML、Binding、dispatcher、未处理异常或崩溃记录；
- 重启后 N.I.N.A. 没有自动重连赤道仪、ATR585M 或平场板，没有曝光、导星或设备运动。

这证明当时的 `0.4.0.8` 源码、构建 artifact 与本机安装版本一致，并通过了启动加载门。操作员随后在同一观测夜重新开顶，Mirfak 的真实闭环又发现并修正了有限黑缝、精确 pixel scale、原生 PHD2 选星和 10 ms 管线问题；这些后续源码改动尚未重新安装，因此不得用早先的 `0.4.0.8` 加载成功替代新版本部署验收。

## 9. 最终云后安全收口（2026-08-24 03:53–04:01 CST）

收到“起云，收口”后立即停止源码以外的一切测试，没有再发曝光、选星、导星或入缝命令：

- PHD2 从 `Guiding` 正常执行 `stop_capture` 到 `Stopped`，随后 `set_connected(false)`；最终 event server 实报 `Stopped`、设备连接 `false`。
- Gemini 主镜盖在赤道仪停放前收到关闭命令并独立回读 `CoverState=Closed`、灯关闭；到位后才执行 Park。断开平场板后 API 只能显示 `Unknown`，不得用断开后的未知覆盖掉断开前的 `Closed` 证据。
- OnStep 由 N.I.N.A. 正常 Park；驱动最终在断开前明确回读 `AtPark=true`、`Slewing=false`、`TrackingEnabled=false`，高度约 `33.376°`、方位约 `0.192°`。断开后的通用 API 不再能证明 `AtPark`，以断开前原子回读为终态证据。
- ATR585M 无曝光。N.I.N.A. 的 5 分钟 Warm 阶梯把内部 set point 从 `10°C` 提升到 `20°C`，但公开字段仍冲突为实际温度 `0°C`、Target `-10°C`、制冷功率约 `63.9%`，且未自动关闭 Cooler。按既有 SOP 正常断开相机；最终 `Connected=false`、`IsExposing=false`、`CoolerOn=false`，没有伪称真实传感器已经达到 20°C。
- QHYminiCam8M 无活动 job，由 QHY service 正常断开；最终 `connected=false`、无 last error。断开后滤轮实体位置不再可实报，不能把先前旧标 R/位置 5（实际 i′）当作断开终态。
- UVEX4 只读终态为 COM5 `Ready`、轮位 2、位置可信、狭缝照明 `Off`、无 last error；收口没有移动狭缝轮、光栅或 M2。
- N.I.N.A.、PHD2、UVEX/QHY 服务进程均正常保留，没有强制杀进程或重启。
- N.I.N.A. Dome/Roof、Safety Monitor 和 Weather 均未连接；软件不能关闭或验证屋顶。上述状态表示设备已达到可关顶条件，屋顶仍需操作员人工关闭并确认。

## 10. 天亮前快速复现与最终收口（2026-08-24 04:10–04:48 CST）

操作员在同一观测夜再次确认云隙出现并人工开顶。本轮不再重新研究对焦，也不使用自写星点排序或自写常规指向；目标是从已停放设备快速复现“成熟指向/解析 → 原生选星 → 自动入缝”，并用 fresh WCS 验证前一轮修复不是偶然。

### 10.1 成熟路径恢复与两级 WCS

- N.I.N.A. 连接 exact OnStep、Gemini cover 和 ATR585M 后，使用其目录坐标动作指向 Mirfak。赤道仪 J2000 回读距目录约 `2.7 arcsec`，但该数值没有被当作光轴居中证明。
- QHYminiCam8M 由自己的 service 在旧标 R/位置 5、实际 Sloan i′ 拍摄 4 张 `5 s` 帧。首张 QHY WCS 距目标 `1544.62 arcsec`，再次证明“赤道仪坐标很准”不能替代广角镜实测 WCS；N.I.N.A. 完成 3 次有界修正，累计实际约 `1505.831 arcsec`，最终 QHY WCS 残差 `8.196 arcsec`。4 帧分别检测 `166/179/184/183` 星，均无质量标志。
- 紧接着的 G3 `2 s` fresh WCS 把 Mirfak 投影到 `(601.939,1084.085)`，目标刚在 1080 高度画面下缘之外。由该 fresh G3 解算生成约东 `156.521 arcsec`、北 `194.874 arcsec`、总量 `249.949 arcsec` 的一次 N.I.N.A. 目录坐标动作；动作后 fresh G3 WCS 把目标投影到 `(990.172,508.977)`。
- 同一 G3 帧中约 `86.3%` 的普通候选被判饱和，因此该帧明确不是主镜或导星镜对焦证据；程序没有借机重新进入对焦扫描。

### 10.2 PHD2 原生选星、可用最优校准与有限缝终验

- PHD2 在自己拥有的全画幅 G3 中原生执行 `find_star`，返回 `(292.60,437.12)`；该点距有限狭缝约 `320.36 px`，不是 Mirfak 光晕，也没有由协调器另排一颗星替换。
- 旧校准超过生产 30 分钟年龄后只进行一次受控重校准。新校准为 RA `-127.3° / 11.434 px/s`、Dec `-13.4° / 26.969 px/s`、正交误差 `23.9°`；它不够优秀，但在显式 `30°` commissioning 上限、exact 身份、11 帧 settle、0 丢帧和后续 fresh residual 的共同约束下属于“当前可用候选”，没有因超过任意 10° 常量而直接拒绝。
- fresh 导星帧把目标投影到 `(1020.838,451.162)`。有限黑缝中心线端点约为 `(1022.384,418.132)`，初始最近点残差 `33.066 px`；目标已经超过中心线端部投影，所以本次正确目标是有限端点，不是无限直线或历史缝中心。
- exact-lock 先执行 `12 + 12 + 9.06 px` 三段，每段都完成 2 帧 settle、0 丢帧并保留 fresh frame；第二张可解算终验帧的目标—端点残差为 `2.7885 px`。随后只作一次 `2.7885 px` 微调，最终 lock 为 `(296.76,403.11)`。
- 最终 fresh PlateSolve3 把 Mirfak 投影到 `(1021.545,418.696)`；到有限端点的残差为 `1.0104 px`，按当帧比例为 `0.3877 arcsec`。它同时通过纯半缝宽 `1.75 px` 和含既有不确定度的保守门。PHD2 在终验后仍为 `Guiding`。

### 10.3 未重复的拍摄块与发现的门禁错误

本轮准备把 QHY `8×5 s` 与 ATR `3×10 s` 并行启动时，临时 orchestration 预检在 ATR 实际 `-9.9 °C`、实际 set point `-10.0 °C`、冷却开启且空闲的情况下，仍把 Advanced API `AtTargetTemp=false` 当成硬失败。该派生字段要求当前温度与 set point 严格相等，0.1°C 的正常量化抖动即可翻转。

预检在创建 QHY job 和调用 N.I.N.A. capture 之前退出，因此本轮**没有新增 QHY job、没有新增 ATR 曝光、没有不明后台采集**。这不覆盖 03:28 CST 已完成的 ATR/QHY 同时拍摄闭环；它新增的是第二次自动入缝复现，以及一个已被设计规则排除的错误温度门。后续温度通过必须使用实际温差、实际 set point、`CoolerOn` 与连续遥测，不使用 `AtTargetTemp`。

### 10.4 太阳升起后的最终安全状态

- PHD2 先接受 `stop_capture`，过渡完成后明确回读 `Stopped`，随后 `set_connected(false)`；最终 raw RPC 为 `AppState=Stopped`、`Connected=false`。
- Gemini cover 只收到一次关闭命令。驱动短暂回报 `NeitherOpenNorClosed`，程序没有重复发送运动；继续只读等待后明确得到 `CoverState=Closed`、`LightOn=false`、亮度 0。
- 只在盖镜到位后执行 OnStep Park；断开前原子读回为 `AtPark=true`、`Slewing=false`、`TrackingEnabled=false`，高度约 `33.376°`、方位约 `0.192°`。随后赤道仪与平场板从 N.I.N.A. 正常断开。
- QHY service 证明 20 个最近 job 均为终态、无活动 job 后正常断开；最终 `connected=false`、无错误。ATR 无曝光，本次重连后的温度遥测从 `-9.9 °C` 连贯升至环境附近，TEC 关闭后在 `IsExposing=false` 下正常断开；最终 `Connected=false`、`CoolerOn=false`。
- UVEX4 最终仍为 COM5 `Ready`、狭缝轮位 2、位置可信、狭缝照明 `Off`、无错误；没有移动光栅、M2 或狭缝轮。
- 官方 [ATR585M 手册](https://www.touptek-astro.com/dl_manual/ATR585M_en.pdf) 只规定 PID 直接温控，没有强制阶梯升降温。本次阶梯回温是在操作员补充该规则之前启动；今后的默认 SOP 不再重复它。
- 软件侧只证明设备已达到可关顶条件；屋顶仍需操作员用原控制链关闭并确认，不能由本记录替代。
