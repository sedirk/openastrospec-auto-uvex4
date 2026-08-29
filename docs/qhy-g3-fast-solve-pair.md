# QHY / G3 快速同指向双解算

## 目的

当 C11/G3 取得一次可信 WCS 后，系统立刻取得同一赤道仪指向下的
GS350/QHY WCS，并比较两个解算中心。这样可以把现场真实的两镜光轴关系
记录成天球切平面上的东/北偏移，而不需要用 `+E/-E/+N/-N` 探针去推断
传感器旋转，也不需要把某一晚测得的偏移写死在代码里。

这条路径优化的是 **QHY 粗居中后、第一次 G3 解算前后的跨光路预定位**。
它与最终目标入缝使用的 `G3PixelToMount`、PHD2 calibration/exact-lock 和
狭缝像素坐标是三套不同的证据与权限，不能混用。

## 最快时序

生产 runner 在成功 G3 WCS 的 mount-binding 通过后、任何基于该 WCS 的
赤道仪移动之前执行以下步骤：

1. 读取最近一次 QHY accepted frame、QHY WCS 和曝光两端的 mount binding。
2. 如果 QHY 曝光中点年龄不超过 `MaximumCachedQhyAge`，两帧曝光中点差不
   超过 `MaximumPairMidpointSeparation`，且完整身份、哈希和 mount binding
   仍有效，则直接复用；不增加曝光。
3. 否则由 QHY sole-owner service 只拍一张 `QuickQhyExposureSeconds` 的帧，
   `MaximumAttempts=1`，只做一次解算。这里没有曝光梯，也没有第二次重试。
   如果 G3 曝光和解算已经耗尽整组时限，则在启动 QHY job 前直接跳过，避免
   为一个必然过期的候选浪费额外曝光。
4. 重新读取一次 mount 状态，复核 G3 曝光前/后、QHY 曝光前/后及最终读回
   共五个坐标。全部必须具有同一坐标历元、同一已知 pier side，任意两次
   实报的球面距离不得超过 `MaximumMountSpanArcseconds`。
5. 重新计算两张 FITS、两份 WCS evidence 和 mount binding 的 SHA-256，随后
   写入不可变 `QhyToG3TransferCandidate`。

默认 Profile 值为：缓存 15 s、曝光中点差 20 s、从最早曝光开始到 Candidate
落盘前建模时刻的整组 30 s、mount span
2 arcsec，以及缓存未命中时的一张 2 s QHY 曝光。它们是可见、可修改并被
action-configuration SHA 锁定的 commissioning 参数，不是隐藏常量。根据
2026-08-28 大丰实机路线验证，项目与新生成 commissioning bindings 的默认值为
`QhyG3FastPairEnabled=true`。它只建立 `MotionAuthority=false` 的单样本候选，
不会授予跨镜移动权限；操作员仍可在高级设置中关闭，并显式保存到当前
N.I.N.A. Profile。旧 bindings 中的保守 `false` 由当前默认运行偏好覆盖，避免
重新导入旧包时静默关闭该机会式采集。

## 映射定义

两次 WCS 都是在同一赤道仪姿态下测得。令 `Q` 为 QHY WCS 中心、`G` 为
G3 WCS 中心，使用版本化 `ICRS-GNOMONIC-TAN-V1` 投影计算：

```text
offset = tangent(Q -> G) = (G3MinusQhyEast, G3MinusQhyNorth)
preposition = -offset
```

因此，如果目录目标已经位于 QHY 中心，将 C11/G3 中心预置到同一目录目标
所需的预测天球修正就是 `preposition`。记录同时保存两幅 WCS 的 pixel scale、
position angle、parity 以及相对比例/旋转；传感器发生旋转时，这些字段会改变
并进入后续适用性判断，中心偏移本身仍由天球坐标直接得到。

单样本的不确定度使用两幅 WCS 各一个像素尺度与观测到的最大 mount span 的
平方和开根号作为保守下界。它不冒充多姿态拟合残差，也不因检测到更多星点而
被错误地除以样本数。

## 权限边界

快速配对输出固定为：

- `Lifecycle=Candidate`
- `SampleCount=1`
- `MotionAuthority=false`
- `CreatorMethod=automatic-same-pointing-paired-wcs`

因此一次配对已经能立即给出本次姿态下的光轴偏差测量，但不能直接成为自动
跨镜运动权威。热形变、挠曲、时角、赤纬、pier side、拆装和相机旋转都可能
改变关系。候选必须按 ADR-0004 聚合代表性样本，验证适用范围、残差、协方差、
安装纪元和过期策略，再生成新的 `Verified/Active` 版本。当前 runner 尚未提供
Active 记录的导入/激活路径，所以 `WideToSlitTransferMode` 仍必须是 `Skip`。

每个通过完整性检查的 Candidate 会同时自动进入本机安装指纹隔离的候选档案：
`%LocalAppData%\UVEX-ADV\calibration\qhy-g3\<hardware-fingerprint>\`。程序保留带
时间戳和 Candidate SHA-256 的不可变 JSON，并原子更新 `latest-candidate.json`
索引；操作员不再需要把测得的东/北偏移手抄到 N.I.N.A. Profile。该索引仍为
`MotionAuthority=false`，不会静默把单样本升级成 Active 运动模型。

当赤道仪因无机械零位、断电或人工设零而自报错误绝对坐标时，本轮新鲜且与
当前未移动指向绑定的 QHY/PL3 正式 WCS 还会直接成为 G3 PL3 的天空提示。
这样目标/赤道仪相差十几度不会被误叫作“两镜光轴差”，也不需要人为扩大
G3 提示可信范围。若 QHY WCS 与赤道仪自报坐标的差异超过该可信范围，runner
在无 park/slew/pulse-guide、pier side 未变且全部哈希有效时，每轮最多通过
N.I.N.A. telescope mediator 执行一次 `Sync`，随后要求 5 arcsec 内的新读回；
Sync 本身不产生 Slew。读回通过后，程序重新执行一次原计划的目录转向，并由
下一张 fresh G3 WCS 验证光学到位；这不是 QHY 精修或搜索循环。小于阈值时
只用作 G3 提示，避免两镜约数角分的正常光轴差造成反复同步。只有同一未移动
指向的 QHY/G3 WCS 差才会进入上面的自动候选档案。

候选也绝不能替代：

- G3 目标质心到狭缝的像素残差；
- `G3PixelToMount` 或 PHD2 exact-lock 的最终入缝闭环；
- fresh G3 后验验证；
- 设备身份、地平线、天气、pier side 和动作预算门。

## 失败与取消

配对是机会式优化，不是 G3 成功路径的硬依赖：

- 缓存无效时可尝试一张 QHY 快帧；
- 单帧质量门或解算失败、超时、哈希变化、mount 漂移、历元/pier 变化，均写
  `qhy-g3-fast-solve-pair-skipped`，然后继续既有 G3 目标识别、WCS 居中或
  有界搜索；
- 配对代码不包含 slew、pulse、PHD2 lock shift 或其他赤道仪命令；
- 用户取消会立即向上抛出，不会被可选失败处理吞掉；若 QHY job 已启动，runner
  只执行相机 job 的有界取消/停止，不发赤道仪动作；
- 任何失败都不会退化成记忆偏移、鬼影猜测或未验证常量。

## 证据

成功时产生 `qhy-g3-fast-solve-pair` JSON，至少包含：

- policy、run/action/preset/Night Setup/installation epoch；
- 两个稳定相机 ID 和两条光路 ID；
- 两张 FITS 与两份 solve evidence 的绝对路径和 SHA-256；
- 两侧曝光开始/中点/结束 UTC、解算完成 UTC 与时间权威；
- QHY/G3 frame 尺寸、ROI、binning、WCS 中心、pixel scale、PA、parity；
- 五个 mount readback、pair source、时间差和最大 mount span；
- 东/北偏移、预测反向预置、协方差、不确定度；
- candidate 自哈希，以及明确的 `motionAuthority=false`。

失败/跳过证据包含失败码、G3 source、零赤道仪命令声明和
`directG3FallbackContinues=true`。G3 stage 的 metadata/metrics 同时显示最近
一次配对 outcome、candidate ID/SHA、时间差、mount span、预测位移和不确定度。

## Commissioning 验收

1. 先在模拟/离线模式验证 policy 非法、缓存过期、时间超限、pier 改变、mount
   span 超限和源文件哈希变化均 fail closed。
2. 真实影子采集阶段启用 `QhyG3FastPairEnabled`，保持
   `WideToSlitTransferMode=Skip`，确认整个配对期间 mount command count 为 0。
3. 分别验收“复用 fresh QHY WCS”和“单张 QHY 快曝”两条分支；确认后者始终
   `MaximumAttempts=1`，失败不进入曝光梯。
4. 在不同赤纬、时角、温度和两个 pier side 收集候选，拆装/旋转后更换
   `InstallationEpochId`。
5. 用独立工具拟合并交叉验证多样本 transfer；在 Active importer、适用性门、
   UI 激活/退役和 fresh G3 后验残差闭环完成前，不得解除 `Skip`。
