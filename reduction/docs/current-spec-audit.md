# `<local-spec-root>` 数据审计（2026-08-14）

## 清点

- 排除 ISIS 自带参考库后，共 101 个实拍 FITS，约 1.576 GiB；
- 全部图像尺寸为 3840×2160，未发现截断或 FITS padding 错误；
- Inspector 的路径优先分类结果：78 light、12 candidate flat、5 dark、6 bias；
- 其中 2 个根目录 8-bit 文件（`1.fit`、`sun.fit`）属于异常/预览数据；
- `DARK/dark.fit` 与 `OFFSET/offset.fit` 是 32-bit ISIS master，其余主流帧为 16-bit。

## Header 风险

- 没有正式的 `Flat Frame` 或 `Arc` 标签；
- 多个 `OBJECT` 沿用旧目标，例如 Castor 文件写成 Jupiter，LED 帧沿用恒星名；
- 缺少 LAMP、SLIT、GRATING、WAVE、RA/DEC 等后续自动化所需关键字；
- 因此不能只依赖 Header 分类，必须保留分类来源和冲突警告。

## 校准帧判断

- 12 个 `LED-*` 呈宽空间照明和平滑连续谱斜坡，不是低背景上的窄发射线；
- 它们可以作为待验证的 flat 候选，但不能当作 arc；
- 当前目录没有可可靠识别的 Ne/Ar/Relco/其他 arc；
- 现有 dark 是 180 s、约 +0.1 °C，而很多 science 是 600 s、约 -10 °C，不应默认缩放套用。

## 推荐连通性样本

- 科学帧：`26.2.18/Regulus-1.fit` 至 `Regulus-3.fit`；
- 候选 flat：同夜 `LED-4.fit` 至 `LED-6.fit`；
- 临时 bias：`OFFSET/offset-1.fit` 至 `offset-5.fit`；
- Regulus 迹线约在 y=704–705，FWHM 约 8–12 px，适合首个端到端样本；
- `26.2.17/castor-1..3.fit` 的迹线 FWHM 约 24–25 px，适合宽/脱焦容错测试；
- M97 很弱且定位有离群点；3C273 没有稳定自动迹线，应该触发低置信度停止和人工 aperture，而不是硬提取。

## 当前可宣称的处理上限

这批数据可以验证：读取、分类、bias、配准、方向翻转、迹线、1D 提取、像素轴 FITS/CSV/PNG 输出。没有真正 arc 时，不能宣称完成物理波长定标；最终产物必须保留 `NEEDS_ARC` 状态。
