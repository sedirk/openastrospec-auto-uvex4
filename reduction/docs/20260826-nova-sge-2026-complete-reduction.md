# PNV J19450648+1822422 / Nova Sge 2026：2026-08-25/26 完整归算报告

**目标坐标（J2000）：** RA `19:45:06.48`，Dec `+18:22:42.2`  
**采集目录：** `<local-toupsky-root>/20260826`  
**光谱设备：** ATR585M + UVEX4，15 μm 狭缝，gain 100，1×1  
**标准星：** Vega，同一设备状态、同一观测夜  
**科学目标采集名：** `Nova Sge 2026`；正式对象标识优先使用 `PNV J19450648+1822422`  
**软件：** OpenAstroSpec Auto — UVEX4 reduction pipeline `0.4.1`  
**结果等级：** 相对响应标定的低分辨率光谱；不是绝对分光光度  
**相关记录：** [同夜早段光谱与 QHY 同步测光报告](20260825-pnv-j19450648-halpha.md)

> **2026-08-26 最终 QHY 勘误：** 软件 `P0…P7` 实际为 `u/OIII/Hα/SII/z/i/r/g`。本夜七张旧 `FILTER=H` 帧位于 P1，实际为 O III；审计未找到本目标的 P2/Hα 帧。因此同步 QHY 的所有 Hα 数值与光谱交叉比较均已撤回。ATR/Vega 光谱归算不受影响。完整证据见[QHYminiCam8M 滤镜身份校正](../../docs/incidents/2026-08-26-qhy-filter-identity-correction.md)。

## 结论

1. **本批 7 张 Nova 光谱已由自动流程完整处理，7/7 接收。** 每张 `600 s`，总积分 `4200 s`；bias、同曝光 dark、LED flat、帧间二维配准、时域 sigma clipping、谱迹、天空扣除、Vega 波长转移和相对响应校正均已执行。原始 FITS 没有被改名、移动或改写。
2. **Vega 标准星给出了可用但仍属转移标定的波长解。** 选择 11 张 `1 s` Vega 帧而不是 2 s 组；自动匹配 4 个吸收特征，内部 RMS `1.735 Å`，模板相关 `0.40597`，输出覆盖 `3908.71–6751.31 Å`。这足以支持低分辨率形态与线区定位，但不等于弧灯级绝对径向速度精度。
3. **目标谱明确是强连续谱上的多重宽吸收/宽 P-Cygni 型结构，而不是行星状星云式窄发射线谱。** Hα 区域存在非常强的宽吸收复合结构，并在红侧恢复；蓝绿区也有大量吸收特征。其总体形态与发现当日的公开 Seimei/KOOLS-IFU 光谱及“接近光学极大、吸收主导”的报告一致。
4. **自动窄线表的 `0 detections` 不表示没有 Hα。** 当前通用 line analyzer 面向窄、高斯型星云发射线；对本目标这种宽、混合、吸收主导的 P-Cygni 轮廓不适用。因此该结果标为 `NOT_APPLICABLE`，不作为科学非检出。
5. **本批不发布新的精确喷发速度或绝对 Hα 线流量。** Hα 的最深结构在转移波长轴上约位于 `6548–6551 Å`，但宽线存在多分量、局部连续谱不唯一，且目标与 Vega 在 15 μm 狭缝中的横向位置差会转化为本报告内部 RMS 未覆盖的零点系统误差。将最深像素直接代入多普勒公式会产生虚假的精度。
6. **同步 QHY 没有取得本目标的 Hα 帧。** 七张旧 `H` 帧实际为 O III；旧 `+0.1085/0.1194 mag/0.9σ`、后续 `+0.06433/0.03561 mag/1.81σ`、约 `+32%` 表观增强、pseudo-EW 与“相容/张力”论证全部撤回。状态是 `NOT_MEASURED`，不是 `NOT_DETECTED`。
7. **可交付产品已进入统一结果目录。** 目标和 Vega 都已加入结果索引生成器；本机正式入口见 `reduction/output/PNV-J19450648+1822422/2026-08-25/` 和 `reduction/output/Vega/2026-08-25/`。

## 1. 外部天体状态

[ATel #18008](https://www.astronomerstelegram.org/?read=18008) 报告在 `2026-08-25.431 UT` 使用 Seimei/KOOLS-IFU 得到的光谱含大量吸收特征；Hα、Hβ 和可能的 He I λ6678 具有 P-Cygni 轮廓，报告的蓝移吸收速度量级分别约为 `−1500`、`−1300` 和 `−1200 km/s`，据此提出该天体很可能是 nova。

[ATel #18009](https://www.astronomerstelegram.org/?read=18009) 给出发现名 `NMW-TTU-26bp`，发现时无滤镜约 `9.0 mag`，位置与 Gaia DR3 `1824166210904571136` 一致；随后测得 `B=8.18`、`V=7.75`、`R=7.40`，之后无滤镜约 `7.14 mag`，说明目标当时仍在增亮并接近可见光极大。

本机光谱在公开参考光谱数小时后取得，重现了“吸收特征很多、Balmer/He 区域宽且复杂”的主要形态。这是独立的定性一致性检查；本报告不替代正式分类公告，也不把外部给出的速度当作本机拟合结果。

## 2. 输入盘点与选择

目录共 `63` 张 FITS：

| 类型 | 目录/模式 | 总数 | 本次使用 | 曝光 | 温度范围 |
|---|---|---:|---:|---:|---:|
| Nova 科学帧 | `Nova Sge 2026/*.fit` | 7 | 7 | 600 s | −9.9…−9.7 °C |
| Vega | `Vega/*.fit` | 22 | 11 张 `1s*.fit` | 1 s | −9.6…−9.5 °C |
| bias | `BIAS/*.fit` | 20 | 20 | 0.0001 s | −9.7 °C |
| dark | `DARK/*.fit` | 4 | 4 | 600 s | −9.6…−8.7 °C |
| LED flat | `FLAT(LED)/*.fit` | 10 | 10 | 5 s | −10.0…−9.9 °C |

### 2.1 为什么 Vega 只用 1 s 组

1 s 组 11 帧的满量程像素比例均约 `0.000012%`，对应同一固定热/坏像素；2 s 组有个别帧的高值尾部明显逼近或进入满量程。标准星的任务是保留宽 Balmer 吸收轮廓与连续谱响应，因此选择动态范围更安全的 1 s 组，不把两档曝光混合叠加。

### 2.2 科学帧是否过曝

7 张 600 s 科学帧的全幅满量程像素比例为 `0.000422–0.000446%`。这些像素数量极少且固定，符合探测器缺陷而不是整条目标谱迹削顶；二维预处理图与一维提取均保留了 Hα 深吸收和连续谱梯度。因此这批数据没有此前科学帧那种全局曝光错误，600 s 对这次 15 μm 缝内目标谱是可用的。

### 2.3 dark 温度

科学帧约 `−9.9…−9.7 °C`，dark 为 `−9.6…−8.7 °C`；最大差约 `1.2 °C`，在本次配置的 `1.5 °C` 上限内。所有 dark 都是同曝光 `600 s`，没有做曝光缩放。

## 3. 自动流程与可复算配置

本次使用的可移植配置已固化为：

- `reduction/configs/20260826-vega-standard.toml`；
- `reduction/configs/20260826-nova-sge-2026-science.toml`。

实际运行时只把 `<local-toupsky-root>` 和 `<local-isis-template-directory>` 替换为本机路径；输出目录由 `full-run` 统一覆盖：

```powershell
uvex-reduce full-run `
  --standard-config reduction/configs/20260826-vega-standard.toml `
  --science-config reduction/configs/20260826-nova-sge-2026-science.toml `
  --template <local-isis-template-directory>/p_a0v.dat `
  --standard-name Vega `
  --target-name "Nova Sge 2026" `
  --output-dir reduction/output/_internal/runs/nova-sge-2026-20260826/workflow `
  --refine-emission
```

`--refine-emission` 的窄线细化没有找到可接受的一致零点修正，因此保持 Vega 转移解；这是正确的安全失败，不是强迫宽 P-Cygni 结构去匹配窄星云线表。

## 4. 校准与提取结果

### 4.1 LED flat 的接收不是盲用

流程分别跑了无 flat 控制组和 flat trial：

| 标准星路径 | 模板相关 | 响应稳健散布 | 匹配线 | 波长 RMS |
|---|---:|---:|---:|---:|
| Vega，无 flat | `0.41935` | `0.02340` | 3 | 无独立冗余 RMS |
| Vega，10 张 LED flat | `0.40597` | `0.03182` | 4 | `1.735 Å` |

模板相关下降仅 `0.01337`，仍高于 `0.35` 下限；响应散布仍在控制组驱动的容许范围内，因此自动流程接收 flat trial。master flat 使用 10 张独立照明归一化帧，有效覆盖探测器 `78.3%`；未覆盖区域保留 mask，不伪造数据。

### 4.2 科学帧配准与组合

| 量 | 结果 |
|---|---:|
| 配置科学帧 / 接收 | `7 / 7` |
| 总积分 | `4200 s` |
| 组合方式 | sigma-clipped mean，`4.5σ` |
| 时域剔除样本 | `594,699`，占 `1.4734%` |
| 最大色散相对位移 | `0.112 px` |
| 最大空间相对位移 | `2.164 px` |
| 有效谱迹 bin | `24` |
| 谱迹检测 S/N | `496.49` |
| 谱迹中心 | `y≈1541.03 px` |
| 空间 FWHM 中位 | `35.01 px` |

这里的 `496.49` 是二维谱迹检测指标，不应误写成每个波长像素的最终科学 S/N。

ASPIRED 自动 optimal extraction 的高频噪声达到独立 boxcar 结果的 `14.5×`，超过 `4×` 质量门；流程自动拒绝该不稳定结果并保留 `aspired-tophat-fallback`。这不是整个归算失败，而是后端在质量验证后回退到更稳健的提取。

### 4.3 波长与相对响应

Vega flat trial 的 4 个波长锚为约：

| 输出像素 | 参考波长 | 残差 |
|---:|---:|---:|
| 89.250 | 3970.07 Å | +1.668 Å |
| 291.144 | 4101.74 Å | −2.812 Å |
| 633.967 | 4340.47 Å | +1.162 Å |
| 3606.093 | 6562.79 Å | −0.019 Å |

二次解的内部 RMS 为 `1.735 Å`，输出方向为蓝到红，有效波长范围 `3908.71–6751.31 Å`。Vega 相对响应的稳健散布为 `3.18%`，最终科学谱有效比例 `97.40%`。

这些量只描述参考星内部拟合。窄缝分光中，目标和 Vega 的缝内横向照明差会造成额外波长零点与通量斜率系统误差；当前没有弧灯，也没有独立的绝对 slit-loss、消光和标准星绝对通量模型，所以 FITS 正确标记为 `FLUXCAL=RELATIVE`、`ABSFLUX=false`。

## 5. 科学光谱解释

### 5.1 接收的结果

- 连续谱在约 4000–6750 Å 可追踪，并含大量真实宽吸收结构；
- Hα 附近不是一根窄发射线，而是跨越数十 Å 的深吸收复合结构和红侧恢复/弱发射成分；
- 蓝端 Balmer 区及可能的 He I 区同样表现出宽、复合轮廓；
- 二维谱中这些结构沿完整目标谱迹存在，不是单个空间像素热柱；
- 当前谱形与 ATel #18008 的“吸收主导、Balmer/He P-Cygni”描述定性一致，也与 ATel #18009 所述接近光学极大的 nova 状态相容。

### 5.2 暂不接受的定量结果

本批最终规范化谱在转移轴上最深的 Hα 结构约落在 `6548–6551 Å`，红侧在约 `6600–6650 Å` 恢复。不能把这两个局部极值机械地当成唯一“吸收谷”和“发射峰”并计算喷发速度，原因包括：

1. 轮廓有多个吸收分量和宽混合，极值不等于拟合中心；
2. 低分辨率连续谱归一化会改变红侧平台的峰位置；
3. Vega→目标的 slit-centering 零点不包含在 `1.735 Å` 内部 RMS 中；
4. 本机和公开参考光谱相隔数小时，目标当时仍在快速演化；
5. 当前无弧灯、无窄天光线或可靠同缝零点将目标轴独立锁定。

因此本报告把外部的 `1200–1500 km/s` 作为公开参考，而不冒充为本机新测量。要从本机数据发表速度，应在下一次取得同设置弧灯，或对多条独立 P-Cygni 轮廓做共同零点与速度分层拟合。

### 5.3 “0 条发射线”的正确语义

最终 `Nova_Sge_2026_emission_lines.csv` 为空，是因为生产分析器要求窄、高斯型、具有一致参考线零点的发射峰。该模型适合 NGC 6543 一类窄线目标，不适合这颗接近极大的吸收主导 nova。正式目录仍保留诊断文件用于可审计性，但文件名明确带 `narrow_model_not_applicable`；任何前端都不应把它显示为“目标没有 Hα”。

### 5.4 与前一段 UVEX 光谱的关系

[前一份报告](20260825-pnv-j19450648-halpha.md) 对更早的 12 张 600 s 数据进行独立探索性提取，其中 7 张在失锁前通过质量门，得到宽 Hα P-Cygni 形态、净发射 EW 中位约 `5.51 Å`，内部散布约 `0.22 Å`，峰—谷探索性速度约 `−1700 km/s`。该分析使用从 5 月转移的局部色散、没有本夜 Vega/flat/dark，因此：

- 宽 Hα 形态仍可作为历史证据保留；
- `EW≈5.5 Å` 只作探索性比较，系统误差远大于内部散布；
- 速度值维持 `NEEDS_REFERENCE`，不与本批直接求“演化差”；
- 本批 7 张后来帧现在已独立、完整归算，不再落在前报告的未处理清单中。

## 6. 同步 QHY 测光合并结论

同夜 QHYminiCam8M 的目标身份仍由 PlateSolve3 与 Gaia 映射验证：旧标 R、实际 i′ 的 WCS 帧中残差为 `0.566 px`（约 `0.97 arcsec`），注册到实际 r′ 参考帧后为 `0.081 px`。`P4–P7=z/i/r/g` 的跨视场星表证据也继续有效。

但最终全圆周实拍显示 `P1=OIII`、`P2=Hα`。本夜七张旧 `FILTER=H` 图像均是 P1/O III，而历史 sidecar 中实际 Hα/P2 的三张测试帧分别属于 M76、NGC 6791 和 M27，没有一张属于 PNV J19450648+1822422。

因此同步 QHY 对本目标 Hα 的状态为：

| 量 | 最终状态 |
|---|---|
| 目标 WCS 身份 | **VERIFIED** |
| `griz` 槽位身份 | **VERIFIED** |
| 七张旧 H 帧身份 | **O III / VERIFIED** |
| 本目标实际 Hα 帧 | **0 / NOT ACQUIRED** |
| QHY Hα excess、非检出或上限 | **NOT MEASURED** |
| QHY 与 UVEX Hα 的相容性/张力 | **NOT APPLICABLE** |

旧 `H-r` 数值不是低显著性 Hα 测量，而是错误带通组合，不能通过增大误差条恢复。原始 FITS 保持不变，后期只通过 SHA-256 sidecar解释真实带通。

## 7. 可交付产品

统一结果目录：

```text
reduction/output/PNV-J19450648+1822422/2026-08-25/
├── final/
│   ├── spectrum.fits
│   ├── spectrum.csv
│   └── spectrum.png
├── diagnostics/
│   ├── preprocessed_2d.png
│   ├── trace.png
│   ├── alignment.png
│   ├── wavelength_residuals.png
│   ├── pipeline_spectrum.png
│   ├── normalized_source.png
│   └── line_diagnostics_narrow_model_not_applicable.png
├── calibration/
│   ├── response.fits
│   ├── response.csv
│   └── response.png
└── metadata/
    ├── processing.json
    ├── workflow.json
    ├── calibration.json
    ├── input_inspection.json
    ├── input_inspection.csv
    ├── line_analysis_narrow_model_not_applicable.json
    └── product.json
```

同夜 Vega 位于：

```text
reduction/output/Vega/2026-08-25/
```

`output/` 是可重建的本机科学产品，按仓库规则不进入 Git；Git 中保留配置、目录生成规则、测试和本报告。

最终标准化产品 SHA-256：

| 产品 | SHA-256 |
|---|---|
| `final/spectrum.fits` | `81E358F6A6E57A6CEA76C4EE930B25E7D5EE9B4CD5E2FA1E07596E494A2FA285` |
| `final/spectrum.csv` | `0F9E3EE68C9B0EA3B22831E582AD74204E3EB5081B4F010AC7F246247A9DEB42` |

## 8. 7 张科学原始帧 SHA-256

| 文件 | SHA-256 |
|---|---|
| `2608252257.fit` | `ED40A958B460C29F1E266A11D1AECDFF350A1D99BDF1FCA1150524030B822754` |
| `2608252305.fit` | `97C756DB6BE5BB139AEE074E05B89253B335ABAE4DB473308976C403CF4603DB` |
| `2608252315.fit` | `C643D63D23CAE82D5E2E3ED5971195A4D8CCE2EA5FFCEE7C6766E1B24A51E95D` |
| `260825232540.fit` | `1C4DCA3075466E91478E658AC4DDB9A5A59E02ED682BD16CF7C35BC69BFB4127` |
| `260825233540.fit` | `9F3CC7A69FBBF5968213E8D22935859C26CEFE292B3443F2E8CF5CB4A89FD514` |
| `260825234540.fit` | `27E8646045005496F7F6400900174E53B9661EC5AB44120915FD2D8518B19FFB` |
| `260825235540.fit` | `60BDDB24965D6B6EA55902C0BDCE44205426F144355657630EB7CF3E089493C7` |

## 9. 下一步科学改进

1. 在目标前后各取得同设置弧灯，或使用可验证的天光/校准灯零点；记录目标、Vega 在缝宽方向的实际位置。
2. 对 nova 增加专用宽线/P-Cygni 模型：共同拟合局部连续谱、发射分量和一个或多个蓝吸收分量，输出模型选择与系统误差，不复用 PN 窄线分析器。
3. 以显式 manifest 比较同夜早段与晚段光谱；在统一 bias/dark/flat、Vega 响应和零点下才报告 EW 或速度演化。
4. QHY 明确选择软件 P2/实际 Hα，先做独立曝光和焦点探针，再使用紧密的 `r′ → Hα → r′` 夹逼循环；取得同 gain/offset/温度的每滤镜 flat，实测 Hα 滤镜透过曲线后再做 synthetic photometry。
5. 为同步成像和光谱写入共同 run ID、目标标准名、UTC 中点和导星质量；避免只能从目录名或 FITS `OBJECT='NA'` 反推身份。

## 10. 最终状态表

| 命题 | 状态 |
|---|---|
| 7 张晚段 Nova 原始谱全部完成校准与提取 | **ACCEPTED** |
| 同夜 Vega 波长转移与相对响应 | **ACCEPTED，RELATIVE** |
| LED flat 优于/不劣于无 flat 控制到可接受范围 | **ACCEPTED** |
| 吸收主导、宽 Balmer/He P-Cygni 型 nova 谱形 | **ACCEPTED（定性）** |
| 本批窄发射线数量为 0 | **NOT_APPLICABLE** |
| 本批绝对 Hα 线流量 | **NOT_MEASURED** |
| 本批精确喷发速度 | **NEEDS_ARC / NEEDS_JOINT_MODEL** |
| QHY 本目标 Hα 测量 | **NOT_ACQUIRED / NOT_MEASURED** |
| QHY 约 +32% Hα excess | **REJECTED** |
| 原始 FITS 完整性 | **PRESERVED** |
