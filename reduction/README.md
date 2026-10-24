# OpenAstroSpec Spectral Studio — UVEX4（后期光谱处理）

这是同一 Git 仓库中的离线后处理产品，包含共用运行时的观测资料库入口；产品边界与发布入口见
[`products/spectral-studio`](../products/spectral-studio/README.md)。本目录是与设备
控制端隔离的 Python 3.11 工程，不会连接 COM5、相机、PHD2、赤道仪或 N.I.N.A.。
原创源码采用 `GPL-3.0-only`，依赖项和输入观测数据保留各自许可与权利。

当前实现包括：

- FITS 扫描、Header 摘要、基于路径/文件名优先的 Light / Flat / Dark / Bias / Arc 分类；
- bias、受温度/曝光门限保护的 dark、候选 flat、坏点/饱和掩膜；
- 科学帧空间与色散方向亚像素配准，arc 组色散方向配准；
- 可配置水平翻转，并由标准星线序列独立复核实际色散方向；最终波长产物自动统一为左蓝右红；
- ASPIRED 迹线与 Horne86 提取，低置信度时采用稳健宽迹线追踪，ASPIRED 不稳定时有界回退；
- ISIS 本地 Pickles/UVEX 标准星模板 + Balmer 线自动定标、ASPIRED/RASCAL arc 定标或人工线对拟合；
- 内存中的 `specutils.Spectrum1D`，以及 FITS、CSV 和 PNG 诊断产物。

## 光谱项目库：采集、筛片、后期与存储之间的桥梁

默认“项目与运行”按已有自动观测日志组织，支持按观测夜、目标、运行结果、
后期进度和来源分组。查看科学/试拍帧、阶段质量门、失败原因、同步测光/盯缝
证据、关联后期工程及成果；保留独立“文件浏览”，不把两种浏览方式混为一谈。
“目录与容量”显示容量卡片、文件类型占用和登记目录，原始 JSON
仅保留在技术详情页。所有容量都是索引统计，不等于可删除空间。
界面改动必须运行真实窗口布局/主题测试并检查截图，见
[资料库视觉验收](../docs/library-ui-visual-checks.md)。

独立入口 **OpenAstroSpec 观测资料库** 与 Studio 共用本目录固定 Python 环境，
不连接任何设备，不复制或改写原片。运行：

```powershell
.\.venv\Scripts\python.exe -m uvex_reduce.library_ui
# 或从仓库根创建桌面/开始菜单入口：
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-library-shortcut.ps1
```

初次打开会列入本机存在的常规观测/QHY/N.I.N.A. 保存位置；点击“更新索引”才
后台扫描，也可“添加数据目录”加入历史后期输出。扫描只读元数据和 FITS Header，
不读全盘像素、不会跟随目录联接，不哈希全部原片；未变化文件不会重复解析。
SQLite 库与人工标签默认保存于 `%LOCALAPPDATA%/UVEX-ADV/library/catalog.sqlite3`。
`--catalog <path>` 可以指定另一个独立库。**库内人工标签不可从原片重建，应备份库文件**；
文件索引可以重新扫描。备份时先关闭资料库以避免漏掉 SQLite WAL 中的修改。

当前支持：

- 按目标、运行、UTC 日期前缀、相机通道、真实/模拟/未知来源、文件类型和留弃状态筛选，
  分页显示，按容量排序；目录/容量明细不等于可删除容量。
- 原始二维图、已有一维光谱/相对定标产品、测光 CSV 曲线、预览图及文本日志回看。
  FITS 先有界采样，再生成内存显示图；不批量落盘缩略图，不替代科学处理。
- “不采用”仅是资料库标签：**未删除、未移动、未进入 Windows 回收站，释放 0 B**。
  与采集质量、后期阶段和原位存在/缺失分开显示；原文件路径可复制，且可直接
  在 Windows 文件管理器中选中原文件。缺失不代表已删除或已回收。
- 0–5 星级、备注独立保存；不改 FITS Header、不覆盖采集质量判定。
- 光谱选片篮显式指定 science/standard/bias/dark/flat/arc；未知通道需人工确认，
  测光与盯缝帧不进入光谱提取。多目标保留逐帧身份。
- “保存工程 → 打开后期”保存 SHA-256 绑定的 `.astroproj` 精确清单，不自动处理。
  Studio 保存/重开及快速/完整工作流都不得从目录 glob 补回弃片；缺失/变化输入会明确报错。
  原有完整历史预设仍要求其标准星/目标匹配；不能拿任意新目标套旧预设。
- 项目页的“科学帧 → 后期”复用同一选片/工程交接，仅选择明确合格且未人工排除的
  光谱原片。旧的手工后期记录也可成为项目；派生 FITS 不是新的科学原片。
  保存后的工程会单文件登记，不自动扫描保存目录内的其他文件；新后期输出目录
  需加入索引再更新。已登记工程由项目刷新检查变更，打开不等于运行处理。
- “项目清理”按项目列出已索引占用、候选、保护文件和原因，先预览逐文件清单，
  再用一个明确标注操作的按钮确认，不用输入口令或摘要。三种方式不混用：仅标记不采用（0 B）、移入 Windows
  系统回收站（同盘仍占空间，0 B）和永久删除（单列预计释放值，不可撤销）。
  不清空回收站；回收失败不降级为永久删除。取消确认不会改动文件。
- 科学/测光/试拍 FITS、校准、后期产品、日志与工程记录保持保护；失败或人工
  不采用不等于可删。优先清理有原片对应、已终止且没有后期依赖的冗余预览。
  执行前复核同一文件身份与依赖；改变文件选择或方式需要重新预览确认。
  持久回执区分已回收、已永久删除、失败/未知；原位状态和人工标签独立显示。
- 归档仍只提供规划，归档盘由操作员以后选择，没有复制/搬迁执行器。

真实与模拟档案可能混存；不能用“失败目标”或目录名称判断可删。
清理保护是对已登记资料的核对，未登记/不明依赖不能宣称安全；未知文件继续保护。
后续归档必须复制校验并
保留路径映射后，再另行确认是否释放本地副本。设计与现有容量盘点见
[`observation-library-and-data-lifecycle.md`](../docs/design/observation-library-and-data-lifecycle.md)。

## 1. 安装环境

ASPIRED 0.5.1 依赖 RASCAL 0.3.10，后者要求 NumPy `<1.24`。因此本工程固定使用独立的 Python 3.11 环境，不能把依赖直接装进最新 NumPy 2.x 环境。

若尚未安装 Python 3.11：

```powershell
winget install -e --id Python.Python.3.11
```

然后从本目录执行以下 pip 命令：

```powershell
cd <OpenAstroSpec 仓库目录>\reduction
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r .\requirements-lock.txt
.\.venv\Scripts\python.exe -m pip install --no-deps -e .
.\.venv\Scripts\python.exe -m pip check
```

只有本机 TLS/证书链异常时，才临时在安装命令末尾加入：

```powershell
--trusted-host pypi.org --trusted-host files.pythonhosted.org
```

## 2. 双击启动 GUI

本机桌面可以创建 `OpenAstroSpec 光谱处理 - UVEX4` 快捷方式。双击进入现代化的七阶段工作区：

1. 媒体与原始数据检查；
2. 主校准（bias / dark / flat）；
3. 二维几何、天空与一维提取；
4. 波长标定；
5. 响应、消光、归一化与二级光谱风险；
6. 科学分析；
7. 交付与归档。

每页中央都有即时 2D 或 1D 可视化。单色 FITS 使用中性灰度的稳健 asinh 拉伸，
不会再用伪彩色暗示不存在的颜色信息；PNG 诊断图保留原图颜色。所有预览均支持
鼠标滚轮围绕光标缩放、左键拖动、水平/垂直滚动条、双击适配，以及工具栏的
`−`、`+`、`适配`、`1:1`。这些操作只改变屏幕显示，不改写 FITS 或科学数据。

工作区把专业路径、可接受降级路径和禁止项分开显示；没有 arc 时可降级到人工
线对、标准星模板或同配置解转移，仍无法建立可信映射时只输出 pixel 轴并标记
`NEEDS_REFERENCE`。没有分光光度标准星时仅建立临时相对响应或连续谱归一化，
不会把结果标成绝对流量。

内置 C11 + CCDT67 + UVEX4i + ATR585M 的 15 / 25 / 35 µm 狭缝预设，也可保存
自定义预设。`.astroproj` 工程文件保存数据与输出目录、完整设备快照、七阶段位置、
选择项、已批准状态、最近产物、二级光谱策略以及人工标定点；已有工程关闭时自动
保存，可以跨夜恢复。

完整流程预设包括：

1. `2026-05 Vega → NGC 6543`：推荐的完整验收组；
2. `2026-05 Vega → HD 140573`：暗弱恒星压力测试；
3. `2026-02 Regulus → NGC 2392`：2 月方向与数据兼容性回归；
4. 按已验收清单处理原始帧完整性、曝光归一、配准叠加、标准星定标、候选平场对照、
   相对响应校正、1D 提取和连续谱归一化；
5. 一键打开 FITS/CSV/PNG/JSON 输出目录。

下方的观测组扫描器仍保留为高级快速提取入口。公开版本不假定任何用户名或
数据位置；请在 GUI 中显式选择“原始数据根目录”和“ISIS 模板目录”。也可在启动前
设置 `UVEX_ADV_DATA_ROOT` 与 `UVEX_ADV_ISIS_TEMPLATE_DIRECTORY`，这些值只属于
本机环境，不应提交到 Git。

若需要重建快捷方式，从项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-reduction-gui-shortcut.ps1
```

也可直接双击项目根目录的 `Install-UVEX-Spectrum-GUI.cmd`。安装脚本会发布并安装
`UvexAdv.Reduction.Launcher.exe` 到当前用户的
`%LOCALAPPDATA%\Programs\UVEX-ADV` 兼容目录，
同时刷新桌面和开始菜单快捷方式。它与前期 Manager 一样直接指向原生 EXE；
Python 运行时路径封装在启动器配置中，桌面不再显示 Python 图标或 CMD 启动目标。

也可以双击项目根目录的 `Launch-UVEX-Spectrum-GUI.cmd`，或运行：

```powershell
.\.venv\Scripts\uvex-reduce-gui.exe
```

GUI 默认会“试算”候选 LED 平场，但只有其标准星模板相关性和响应曲线粗糙度都
通过质量门，且相机型号、gain、binning、ROI 均与科学帧兼容，才会进入首选
产品；勾选不等于强制应用。

短序列可选用简单而可审计的两级降噪：

```toml
[preprocess]
cosmic_ray_clean = true  # 每张先运行保守 L.A.Cosmic
combine_method = "mean"  # 配准、sigma clip 后均值合成
```

默认仍为 `cosmic_ray_clean=false`、`combine_method="median"`。三帧及以上且已完成
单帧宇宙线清理时，均值通常比中位数更充分地降低随机噪声；原始科学谱不会再做
一维平滑。FITS Header 以 `CRREJECT`、`COMBMETH`、`SIGCLIP` 记录处理方式，JSON
也保存相同信息，避免把展示性平滑误当成科学数据。

也可在命令行运行同一完整流程：

```powershell
cd <OpenAstroSpec 仓库目录>\reduction
.\.venv\Scripts\uvex-reduce.exe full-run `
  --standard-config .\configs\20260509-vega-standard.toml `
  --science-config .\configs\20260506-ngc6543-science.toml `
  --input-root "<本地 ToupSky 数据目录>" `
  --template "<本地 ISIS 模板目录>\p_a0v.dat" `
  --standard-name Vega `
  --target-name NGC6543 `
  --output-dir .\output\NGC6543\2026-05-06\runs\workflow `
  --refine-emission
```

5 月实跑结论和质量数字见
[`docs/may-2026-full-run.md`](docs/may-2026-full-run.md)。

运行或重跑后，用下列命令刷新“目标 → 日期”统一交付目录和图形索引：

```powershell
.\.venv\Scripts\python.exe .\tools\organize_outputs.py
```

日常浏览请直接双击 `output/00-打开结果索引.html`；历史流水线目录集中保存在
`output/_internal/`，不会与正式结果混在一起。

## 3. 扫描测试数据

Inspector 可分别扫描 2 月 `SPEC` 或 5 月 `ToupSky`：

```powershell
.\.venv\Scripts\uvex-reduce.exe inspect `
  "<本地 SPEC 数据目录>" `
  --output-prefix .\output\_internal\quality\inspection\spec_manifest
```

只看汇总而不打印 101 行文件表：

```powershell
.\.venv\Scripts\uvex-reduce.exe inspect `
  "<本地 SPEC 数据目录>" `
  --summary-only `
  --output-prefix .\output\_internal\quality\inspection\spec_manifest
```

Inspector 会生成完整 JSON 和 CSV manifest。分类优先级为：

1. 目录及明确文件名（例如 `OFFSET`、`DARK`、`LED-*`）；
2. `IMAGETYP` / `OBSTYPE`；
3. 无可靠信息时标为 `unknown`。

`OBJECT` 与文件名冲突不会被静默接受，而会写入 `warnings`。

## 4. 运行 Regulus 标准星定标

```powershell
Copy-Item .\configs\spec-regulus.toml .\configs\spec-regulus.local.toml
# 编辑被忽略的 *.local.toml，把所有 <local-...> 路径替换为本机值。
.\.venv\Scripts\uvex-reduce.exe reduce --config .\configs\spec-regulus.local.toml
```

示例配置选择：

- `26.2.18/Regulus-1..3.fit`：科学帧；
- `OFFSET/offset-1..5.fit`：临时 bias；
- `26.2.18/LED-4..6.fit`：只登记为候选 flat，默认不应用；
- 不使用现有 dark：跨夜暗场本身允许复用，但这批暗场的曝光和温度与科学帧不匹配；
- 不提供 arc；改用 ISIS `p_b8v.dat` 宽波段模板和 Balmer 系列完成标准星波长定标；
- 自动检测到原方向配置与实拍 Balmer 顺序相反，因此最终 2D/1D 产物会再次反转为波长递增。

当前实拍结果：3854–7473 Å、6 条 Balmer 线、模板相关系数约 0.79、内部拟合 RMS 约 0.11 Å。这个 RMS 只是所用恒星线对同一多项式的内部残差，不代表弧灯级绝对精度；恒星径向速度、狭缝照明、谱线展宽和 Hε/Ca H 混合都可能移动零点。

统一交付目录为 `output/Regulus/2026-02-18/`，包括：

- `final/spectrum.fits`、`spectrum.csv`、`spectrum.png`：统一 1D 产品；
- `diagnostics/`：2D、迹线、对齐和波长残差；
- `metadata/product.json`：输入来源、SHA-256、处理层级和限制；
- 旧的逐次实验运行保存在 `output/_internal/runs/regulus*`。

即使自动追迹失败，`*_preprocessed.png` 也会先写出，便于设置 `manual_trace_y`，不会出现“要求看诊断、但失败后没有诊断图”的死循环。

## 5. 在 Python 中使用

```python
from uvex_reduce import ReductionPipeline, read_spectrum

run = ReductionPipeline.from_config("configs/spec-regulus.local.toml").run()
spectrum = run.result.spectrum  # specutils.Spectrum1D

# 从 UVEX-ADV FITS 重新载入，并保留显式 MASK：
restored = read_spectrum(run.artifacts["fits"])
```

ASPIRED 自身的 `SpectrumOneD` 与 `specutils.Spectrum1D` 不是同一个类；管线会显式转换 wave/flux/uncertainty/mask。非线性波长解存为逐像素表列，不伪装成线性的 `CRVAL/CDELT`。

## 6. 标准星模式和 Arc 模式

标准星配置的核心部分：

```toml
[wavelength]
mode = "stellar_template"
template_directory = "<local-isis-template-directory>"
template_star = "Regulus"
polynomial_degree = 2
minimum_matched_lines = 5
minimum_template_correlation = 0.35
auto_reverse_output = true
```

宽波段 Regulus 数据使用多条 Balmer 线二阶拟合。若某个光栅设置只覆盖 Hα/Hβ，管线只允许在宽模板相关系数至少 0.50、同名 UVEX Hα 模板相关至少 0.65 时降级为两线线性解；此时不会报告没有统计意义的两点 RMS。

标准星定标适合当前数据和自动化初值，但并不取代高精度弧灯。

已生成的标准星 FITS 也可以在配置中复用：

```toml
[wavelength]
mode = "solution_file"
solution_path = "../output/Regulus/2026-02-18/final/spectrum.fits"
```

引用文件与目标必须具有相同的探测器宽度；管线会拒绝不同 ROI/binning。当前原始 Header 没有光栅角度和狭缝信息，因此软件无法自动证明光学配置相同，运行记录会要求人工确认并建议用夜天/地球大气线检查零点。

采集与科学帧同一光学配置（光栅、狭缝、binning、ROI）的 Ne/Ar/Relco 等窄发射线灯后，修改配置：

```toml
[inputs]
arc = ["26.2.18/ARC-[1-5].fit"]

[wavelength]
mode = "aspired_atlas"
atlas_elements = ["Ne", "Ar"]
minimum_angstrom = 3500.0
maximum_angstrom = 7500.0
polynomial_degree = 2
medium = "air"
minimum_matched_lines = 4
maximum_rms_angstrom = 5.0
minimum_pixel_span_fraction = 0.2
```

管线要求最终波长轴严格递增，并检查匹配线数量、像素覆盖范围和 RMS。也可使用 `mode="known_pairs"` 及经确认的翻转后 pixel/wavelength 锚点做开发或人工标定。

## 7. 测试

```powershell
.\.venv\Scripts\python.exe -m pip install pytest==8.3.5 ruff==0.9.10
.\.venv\Scripts\ruff.exe check src tests
.\.venv\Scripts\pytest.exe -q
# 仅在专用测试桌面或已安排的视觉验收时段启用，会显示合成数据窗口：
.\.venv\Scripts\pytest.exe -q --run-ui-tests
```

测试覆盖 LED/脏 Header 分类、水平翻转、显式帧完整性修复、混合曝光
归一、混合 gain 拒绝、亚像素位移、NaN 平场、坏点插值、宽/偏心迹线、空帧拒绝、
正反波长解、标准星模板方向恢复、仿射发射线复标、候选平场质量门、二级光谱诊断、
标准解文件复用、目标身份质量门以及 FITS/Spectrum1D mask 往返。

## 模块边界

- `inspector.py`：发现、Header 读取、分类和 manifest；
- `preprocess.py`：校准、掩膜、配准、叠加和方向修正；
- `extraction.py`：迹线、ASPIRED 提取、质量门和备用提取；
- `stellar.py`：ISIS 标准星模板发现、Balmer 模式识别、全局窄波段回退和方向复核；
- `wavelength.py`：标准星、RASCAL 和人工锚点波长解入口；
- `products.py`：Spectrum1D 与 FITS/CSV；
- `diagnostics.py`：追迹、配准、波长和 1D 图；
- `pipeline.py`：编排、降级策略、运行 manifest；
- `workflow.py`：标准星、平场对照、科学帧、相对响应与归一化的完整编排；
- `cli.py`：`inspect` / `reduce` / `full-run` 命令行；
- `studio.py`：七阶段中文桌面工作区、即时可视化、设备预设与 `.astroproj` 持久化；
- `project.py`：工程文件 schema、设备快照与安全恢复；
- `gui.py`：双击启动入口及兼容的完整流程执行器。

3C 273 实跑报告与人工处理 SOP：

- `docs/20260504-3c273-processing-hbeta-report.md`；
- `docs/manual-reduction-sop.md`；
- PDF 版由项目根目录 `scripts/build_reduction_pdfs.py` 生成到 `output/pdf`。

后续若增加 Web GUI（例如 Streamlit），可直接复用 `ReductionPipeline` 与
`run_full_workflow`，不需要复制算法。

## 参考 API

- [ASPIRED documentation](https://aspired.readthedocs.io/en/latest/)
- [ASPIRED TwoDSpec API](https://aspired.readthedocs.io/en/latest/modules/twodspec_api.html)
- [ASPIRED OneDSpec API](https://aspired.readthedocs.io/en/latest/modules/onedspec_api.html)
- [specutils Spectrum1D](https://specutils.readthedocs.io/en/v1.20.3/spectrum1d.html)
- [ccdproc reduction toolbox](https://ccdproc.readthedocs.io/en/latest/reduction_toolbox.html)
- [specreduce quickstart](https://specreduce.readthedocs.io/en/latest/getting_started/quickstart.html)
