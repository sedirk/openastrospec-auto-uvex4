# 2026-08-19 自动观测闭环实机调试收口

**本地观测夜：** 2026-08-18 晚至 2026-08-19 凌晨（Asia/Shanghai）  
**结论：** 部分闭环通过；未完成“目标入缝 → 稳定导星 → ATR 光谱曝光”，不得登记为端到端验收通过  
**终止原因：** 后段云层使 fresh G3 天空复核为 0 个相干星，Markab 同时接近 45° 启动/前视地平门；继续动作不能产生可接受证据

本记录只总结不可变证据及由此产生的代码要求。原始 FITS、求解结果和运行 manifest 仍位于 Git 忽略的 `output/commissioning/2026-08-19-night/evidence/`，没有被改名、移动或重写。

## 1. 已实际证明的环节

### QHY / GS350 广域链

- QHY 服务保持 QHYminiCam8M 唯一所有权，实体滤轮当时读回 `R/slot 5`；2026-08-26 身份校正后应解释为**实际 Sloan i′/槽 5**。该帧的 WCS/居中见证仍有效，不得称为 r′ 测光。
- Markab 的 QHY 粗解算和闭环居中成功；最后一轮 WCS 到目录目标约 11.3 arcsec。
- 广域粗居中使用独立粗运动门，不复用 G3/狭缝精修门，也没有写入或使用固定两镜光轴差。
- 后续源码已加入可选的快速同指向双解算：G3 WCS 一成功便优先复用新鲜 QHY WCS，否则只补一张短 QHY 帧；输出为带五点 mount bracket 和完整 SHA-256 的 Candidate，零赤道仪命令且不能直接授权预置运动。今晚原始观测发生在该功能安装前，不能追认成正式 paired-WCS evidence。
- 关键运行证据目录：`qhy-coarse-center-markab/pre-phd-loop-refresh-20260818T195142707Z`。

### C11 / G3 指向事实

QHY 居中不等于 C11 居中。上述 QHY WCS 已接近 Markab 时，同一时刻 N.I.N.A. 赤道仪/C11 的 J2000 指向仍约为 `343.632893°, +18.101376°`，距 Markab 约 `3.79323°`。这是该安装纪元的一次现场测量，不是可写进程序的常量。

随后由 N.I.N.A. 以 Markab 目录 J2000 `346.1902227083°, +15.2052671389°` 执行一次受限 C11 目录指向并验收到位。证据目录：`catalog-slew-markab/c11-direct-markab-20260818T200119387Z`。该动作只证明目录指向，不冒充图像 WCS 闭环居中。

### 狭缝几何和主镜焦点

- 九帧 LED OFF/ON/OFF 原始输入保持不变。旧分析中的 `AMBIGUOUS` 来自候选带中心未先精修的实现缺陷；新的派生复算记录引用全部原始 SHA-256，未改写旧结果。
- 派生复算通过：狭缝绝对角约 `-1.5°`，长度约 `405 px`，宽度约 `9.032 px`，confidence 约 `0.895`，uniqueness 约 `1.436`。
- 派生 manifest：`g3-slit-led/derived-reanalysis/g3-led-derived-reanalysis-20260818T125653863Z-a607a46077264dd79befb30c90a67ff9/derived-reanalysis-manifest.json`，SHA-256 `91F29E01FD38CFDAFD7F46596F8D4230F9DF85067CDD1D3D9E2A7A7DC89858E8`。
- 今夜反复对焦没有形成新的生产候选；最终安全恢复并保持 Star Focuser Pro `5000`。没有手选 4850/4950 冒充正式拟合结果。

### PHD2 / G3 协议链

- PHD2 profile 2、G3M2210M、On-Step、gain 100、bin 1 和注册表稳定 USB 绑定已验证。
- 今夜 forced calibration 的实测值约为 RA `65.4° / 23.1 px/s`、Dec `167.1° / 27.9 px/s`、正交误差 `11.7°`。这份校准不能再因单一 `10°` 常数被整体丢弃；它应作为带风险标记的候选，与 settle、fresh residual 和同拓扑证据一起分级。
- 已在 fake PHD2 event server 上证明新的全幅选星接管顺序：`Stopped/Selected → one loop → fresh LoopingExposures → exact=false 选星 → guide(recalibrate=false)`。它不再先进入 Guiding 后尝试跨全幅选星。
- exact runtime lock shift 的目标公式固定为 `desiredGuideLock = guide + (slit - target)`；目标与导星不是同一颗时也不会错误地把 slit 直接当 guide lock。
- PHD2 事件没有 RPC request ID；客户端现以本地 operation/connection/guide epoch 约束 settle，拒绝外部或迟到事件伪造当前成功。

## 2. 明确没有完成的环节

- 没有一帧 fresh G3 同时证明 Markab 目标身份、目标质心、导星质心和最终狭缝残差。
- 没有执行经过生产 runner 验收的 exact-lock 分段入缝。
- 没有形成可用于科学块的最终 settled guide epoch。
- 没有启动 QHY 科学测光块与 ATR 光谱块的共同运行。
- 没有 ATR 科学曝光。ATR Advanced API 仍出现温度 `0 °C`/目标 `-10 °C`/功率非零的失真遥测，不能仅凭 `AtTargetTemp` 放行科学帧。
- 屋顶开启来自现场操作员陈述；系统仍没有可复用的 SafetyMonitor/roof/weather 自动证明，因此本轮不是无人值守资格测试。

## 3. 云层下的 G3 诊断不得被误用

C11 目录指向 Markab 后取得的 500 ms、2 s 和 10 s 帧没有形成可信 Markab WCS。局部搜索中一度在东向位置检测到少量源，随后 500 ms/2 s 复核又降为 0 星；最后的不可变天空复核 SHA-256 缩写为 `2BC562A1…55A34B`，检测 0 个相干星。该时间序列与云层重新遮蔽一致，不能用来：

- 固化 QHY→G3 光轴差；
- 复用旧目标像素坐标；
- 把鬼影或热像素声明成 Markab；
- 放宽质心唯一性门；或
- 断言 G3 长曝光解析路线本身无效。

## 4. 由本夜证据确定的生产路线

正式入口必须是 N.I.N.A. 插件/Advanced Sequencer；`tmp/` 工具只用于有界 commissioning，不作为日常操作面。

```text
N.I.N.A. 目录指向 C11
    ↓
QHY 广域 WCS（只证明 GS350 状态，界面单独显示）
    ↓
可选且当前有效的 QHY→G3 预置；否则显式 Skip
    ↓
G3 参数化 WCS 曝光阶梯
    ├─ WCS 成功且目标在场内 → 目标识别
    ├─ WCS 成功但目标在场外 → N.I.N.A. 有界 WCS 居中 → fresh G3 重解
    └─ WCS 失败 → 有界小步搜索 → 每点 fresh 长曝重解 → 失败耐久回原点
    ↓
选择 fine-motion authority
    ├─ 首选：分级 PHD2 校准 + operation-bound settle + exact-lock 分段
    └─ 后备：独立、版本化、可失效的四向 G3PixelToMount 标定
    ↓
每段 fresh G3 目标/狭缝/guide residual
    ↓
导星稳定 → QHY 测光 → ATR 曝光阶梯/科学块
```

曝光用途必须分开：

- 最短的毫秒级曝光只供超亮目标未饱和翼部质心/直接目标导星，永不用于焦点；
- 约秒级曝光优先寻找普通、独立的导星星；
- 参数化的较长曝光阶梯供 WCS 解算和无星场恢复。

具体毫秒数或 `2/5/10 s` 只能是该 G3 profile 的版本化 commissioning preset，不是跨设备源码常量。

## 5. 下一晴夜的最短验收顺序

1. 选择高于启动门且能覆盖完整最坏耗时的目标；先确认实时透明度，而不是复用本夜云中帧。
2. 由 N.I.N.A. 完成 C11 目录指向；QHY 与 C11 的“已居中”状态分别显示。
3. 运行 G3 WCS 曝光阶梯。任何成功邻场 WCS 立即转入 N.I.N.A. 有界居中并 fresh 重解，不继续盲搜。
4. 以约秒级帧选择普通导星；若确实只有唯一超亮目标，则显式进入短曝光 `DegradedDirectTargetGuiding`。
5. 对 PHD2 当前暴露的 active calibration 按多维策略生成质量等级，而不是只比较一个角度；生产路径尚未加载历史候选库。每个 exact-lock 分段之后都等待同一 operation settle 并保存 fresh G3 residual。
6. 只有 `target/slit`、`guide/lock` 和 flux/形态门同时通过，才开始 QHY 测光。
7. 先修复并验证 ATR 温度遥测；连续真实温度/设定点/功率证据通过后，才允许 ATR probe 和科学曝光。
