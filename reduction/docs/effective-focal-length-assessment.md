# C11 + CCDT67 有效焦距复核

## 结论

对本机保留的 2026-05-16 G3M2210M 导星帧作 Gaia DR3 五恒星匹配后，得到：

| 参数 | 结果 |
|---|---:|
| 3×3 合并后板尺度 | 1.14956 arcsec/px |
| 原始 4 µm 像元板尺度 | 0.38319 arcsec/px |
| 有效焦距 | 2153.2 mm |
| 相对 C11 名义 2800 mm 的倍率 | 0.76898× |
| 等效焦比（口径 279.4 mm） | f/7.71 |
| 五星拟合 RMS | 0.57 个 3×3 像素 |

工程预设采用 **2150 ± 20 mm、f/7.7、0.769×**。2153.2 mm 是该帧的拟合值；
±20 mm 是考虑导星光路畸变、星像质心、像元标称尺寸以及 SCT 调焦/后焦变化后
采用的实用不确定度，不应把形式拟合的小误差误认为跨夜绝对精度。

## 为什么不采用 PHD2/N.I.N.A. 中的焦距

- PHD2 设备配置中的 2150 mm 是用户手填值；PHD2 显示的 0.38 arcsec/px 是由该值
  和 4 µm 像元计算出来的，不是独立测量。
- N.I.N.A. 日志中 C11/G3M2210M 使用 2000 mm、12 µm（3×3）发起的板解明确失败。
- 同一批日志里的成功板解均为另一套 350 mm、2.9 µm 相机，不能用于本光路。

因此本结论不使用上述配置值，只使用原始导星帧中的恒星几何关系。

## 数据与方法

源帧：

`%LOCALAPPDATA%\NINA\PlateSolver\Failed\20260516-233118.11008..fits`

FITS Header 给出 G3M2210M、3×3 binning、合并后像元 12 µm。自动板解当时只检测到
少量目标并失败，但图中五个真实星像可与 Gaia DR3 形成唯一的五点相似变换；下一
个不同候选最多只能匹配四点。Gaia 位置按自行近似传播到 2026.37 历元后，对天空
切平面坐标与图像质心拟合旋转、平移和统一尺度。

使用的关系为：

`f(mm) = 206.264806 × pixelSize(µm) / scale(arcsec/px)`

代入原始像元 4 µm 和板尺度 0.383186 arcsec/px：

`f = 206.264806 × 4 / 0.383186 = 2153.2 mm`

逐一删去一颗匹配星重拟合时，焦距范围为 2150.2–2155.3 mm。匹配表、数值结果和
覆盖图分别保存为：

- `reduction/output/_internal/quality/focal-length-study/effective-focal-length-matches.csv`
- `reduction/output/_internal/quality/focal-length-study/effective-focal-length-result.json`
- `reduction/output/_internal/quality/focal-length-study/effective-focal-length-gaia-validation.png`

## 与 0.75×记忆的关系

C11 官方名义参数为 2800 mm、口径 279.4 mm。若 CCDT67 恰为 0.75×，理论值应为
2100 mm、f/7.52。Astro-Physics 说明 CCDT67 后法兰到焦面约 50 mm 时可预期 0.75×；
但它同时明确说明 SCT 的移动主镜会随后焦改变原始焦距，实际倍率必须实测。

本次 2153 mm 对应 0.769×，与“约 0.75×”的记忆方向一致，差异仅约 2.5%，并且
比把产品型号直接当作固定 0.67×更符合这套实机光路。

- Celestron C11 参数：https://www.celestron.com/products/c11-optical-tube-assembly-cge-dovetail
- Astro-Physics CCDT67 参数：https://www.astro-physics.com/ccdt67
- Astro-Physics 缩焦计算说明：https://www.astro-physics.info/tech_support/accessories/photo_acc/telecompressor-techdata.pdf

## 对狭缝和既有判断的修正

按 2153 mm 计算，焦面上的名义角宽为：

| 狭缝 | 天空角宽 |
|---|---:|
| 15 µm | 1.44 arcsec |
| 25 µm | 2.39 arcsec |
| 35 µm | 3.35 arcsec |
| 300 µm | 28.74 arcsec |

因此先前报告中的“约 f/6.7”和“35 µm 约 3.85 arcsec”均已作废。实测 f/7.7 只比
f/8 略快，削弱了“严重快速光锥失配导致 NGC 6543 左肩”的解释权重。但 UVEX
官方另有一项与焦比不同的限制：为保持宽波段无色差，建议不用减焦镜/平场镜。
所以 CCDT67 仍应尽早做原生 f/10 A/B；若有影响，更合理的路径是狭缝前色差、
离轴像差或照明变化，而不是仅凭产品型号认定的 f/6.7 过快或已证实的固定鬼像。

这项焦距修正也不等于 UVEX 内部光学状态合格。同夜 Vega 点源后来测得空间 FWHM
由约 4820 Å 的 9.68 px 增至约 7077 Å 的 17.73 px，六张单帧趋势一致；按照
UVEX 官方调校判据，这更直接指向 M2 镜架角度/整体像散状态需要复核。M1 彗差、
目标是否位于该光栅的最佳 Y 以及 CCDT67 狭缝前像质应通过分步灯谱 A/B 区分。
