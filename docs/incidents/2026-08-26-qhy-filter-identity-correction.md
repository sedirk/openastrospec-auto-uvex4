# 2026-08-26 QHYminiCam8M 滤镜身份校正

**最终状态：** 八个软件位置均已由实拍闭合。仓库配置、历史 sidecar 与报告按最终映射修正；原始 FITS 保持不可变。正在观测的 N.I.N.A./QHY 实例没有为本次勘误而重启，已安装态需在观测结束后部署并读回确认。

**关键科学影响：** 2026-08-25 新星数据中写作 `FILTER=H` 的七张窄带帧实际是 **O III**，不是 Hα。因此此前所有基于这些帧的 `H-r`、Hα excess、显著性、pseudo-EW，以及它们与 UVEX Hα 光谱的比较，全部撤回。ATR585M/UVEX 光谱不受影响。

## 1. 最终映射

物理滤镜盒按最初标签顺序排列为：

```text
物理 1/2/3/4/5/6/7/8 = u/g/r/i/z/SII/Hα/OIII
```

软件零位和转动方向不同，实测软件位置为：

```text
P0/P7/P6/P5/P4/P3/P2/P1 = u/g/r/i/z/SII/Hα/OIII
```

等价地，按软件位置升序：

```text
P0/P1/P2/P3/P4/P5/P6/P7 = u/OIII/Hα/SII/z/i/r/g
```

若物理标签号记为 `n=1..8`，软件位置为 `p=0..7`，关系为：

```text
p = (1 - n) mod 8
```

这说明盘片不是随机错装，而是**软件坐标相对物理标签倒序并循环平移**。

| 软件位置 | 旧 N.I.N.A./FITS 名称 | 实际滤镜 | 建议标准名 | 旧焦点补偿 |
|---:|---|---|---|---:|
| 0 | `S` / `Slot 0` | Sloan u′ | `U` | 0 |
| 1 | `H` / `Slot 1` | O III | `O` | −200 |
| 2 | `O` / `Slot 2` | Hα | `H` | 0 |
| 3 | `U` / `Slot 3` | S II | `S` | −62 |
| 4 | `G` / `Slot 4` | Sloan z′ | `Z` | +205 |
| 5 | `R` / `Slot 5` | Sloan i′ | `I` | 0；自动对焦参考 |
| 6 | `I` / `Slot 6` | Sloan r′ | `R` | −120 |
| 7 | `Z` / `Slot 7` | Sloan g′ | `G` | −129 |

焦点补偿属于盘片位置；改科学名称时不得把补偿值随名称搬到另一槽。

## 2. 实拍证据

### 2.1 `P4–P7`：Sloan `z/i/r/g`

两套独立星场均由 PlateSolve3 解算并与 Pan-STARRS 星表匹配：

- 第一视场：414 个匹配，WCS 中位残差 `0.433 px`，95 分位 `0.813 px`；
- NGC 6791：394 个匹配，中位残差 `0.389 px`，95 分位 `0.774 px`；
- 最优排列 `P4=z, P5=i, P6=r, P7=g` 的稳健颜色散布为 `0.3178 mag`；
- 旧软件声明排列的散布为 `0.6505 mag`，约差 `2.05×`。

因此 `P4–P7=z/i/r/g` 是跨视场复现的结果，不依赖滤镜名称预设。

### 2.2 `P0–P3`：u 与三窄带

NGC 6791 同一视场实拍显示：

- `P1` 的恒星颜色最接近 g/O III；
- `P2`、`P3` 都显著偏红，符合 Hα/S II；
- `P0` 因焦差表现为明显环状星像，大孔径通量不呈红带行为；结合已知八片滤镜集合，余项为 u。

为避免仅凭恒星颜色混淆 Hα 与 S II，又在 M27 上做了空间形态测试：

- `P1` 强烈显示 M27 的平滑主亮体，判定为 O III；
- `P2` 清楚显示哑铃状外壳，判定为 Hα；
- `P3` 星云响应显著更弱，判定为 S II。

`P1` 前后两张星场归一化结果只差约 `0.6%`，说明这一判别不是透明度突变造成。机器可读诊断位于 Git 忽略的：

- `output/analysis/2026-08-26-night/qhy-filter-positions-0-7-ngc6791.json`；
- `output/analysis/2026-08-26-night/qhy-m27-narrowband-filter-identity.json`。

## 3. 历史数据处置

原始 FITS 没有被改名、移动、重写 header 或重新保存。`reduction/tools/qhy_filter_identity_correction.py` 按相机稳定身份与原记录标签生成 SHA-256 sidecar，后期必须消费 sidecar，不能根据旧文件名猜带通。

本次最终扫描结果：

| 项目 | 数量 |
|---|---:|
| 检查的 FITS 路径 | 2101 |
| 确认为本机 QHYminiCam8M 的路径 | 420 |
| 受错误标签影响的路径条目 | 404 |
| 按 SHA-256 去重后的独立帧 | 305 |
| 无法读取的 FITS | 0 |

旧标签计数：`G=19, H=28, I=16, O=3, R=283, S=2, Slot3=4, Slot4=3, Slot5=2, Slot6=2, Slot7=2, U=21, Z=19`。

按最终物理带通计数：`G=21, H=3, I=285, O=28, R=18, S=25, U=2, Z=22`。

sidecar 位于 Git 忽略的：

- `reduction/output/_internal/provenance/qhy-filter-identity-correction-20260826.json`；
- `reduction/output/_internal/provenance/qhy-filter-identity-correction-20260826.csv`。

## 4. 对 2026-08-25 新星结果的影响

七张原标 `FILTER=H`、15 s 的新星帧来自软件 `P1`，实际是 O III。当前审计中实际 Hα 对应的旧 `FILTER=O`/`P2` 只有三张测试帧，目标分别为 M76、NGC 6791 和 M27；**没有一张是 PNV J19450648+1822422 / Nova Sge 2026。**

因此以下结果全部为 **REJECTED / WITHDRAWN**：

- `Δ(H-r)=+0.10853 mag`、散布 `0.11942 mag`、`0.9σ`；
- 后续所谓“校正值” `Δ(H-r)=+0.06433 mag`、散布 `0.03561 mag`、`1.81σ`；
- 约 `+32%` 的 Hα 表观增强；
- 任何由这些帧计算的 Hα flux factor、pseudo-EW 或 Hα 上限；
- QHY Hα 与 UVEX Hα P-Cygni 轮廓“相容”“不相容”或“存在张力”的论证。

这些量不是精度不足，而是**带通身份错误导致测量对象错误**。七张帧仍可作为 O III commissioning 数据重新分析，但不能追溯性改称已取得 Hα 测光。

UVEX/ATR 光谱的二维像素、Hα 宽轮廓、Vega 转移标定和归算产品完全不依赖 QHY 滤镜轮，因此继续有效。

## 5. 软件与安装态

- 仓库 `config/qhy.production.json` 使用 `U0,O1,H2,S3,Z4,I5,R6,G7`；
- 历史校正工具使用 `S→U, H→O, O→H, U→S, G→Z, R→I, I→R, Z→G`；
- N.I.N.A. profile 修复脚本使用 `U,O,H,S,Z,I,R,G`，并保留各软件位置原有焦点补偿；
- 已经运行中的观测实例不会为此次勘误中途重启；部署后必须断开状态启动、连接一次、读回八位置名称并拍一轮短验证帧；
- 新采集必须同时保存 `softwarePosition`、原始 `FILTER` 和解析后的物理带通。身份不一致时应阻止科学归算，而不是改写原片。
