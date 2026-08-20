# 二级光谱污染诊断

UVEX-ADV 不把 6800 Å 当作已经测得的污染起点。当前输出采用三层含义：

- `ORD2STRT=6800`：保守预警阈值，可配置；只设置 `ORDER2_RISK`，不进入坏像素 `MASK`，也不删除数据。
- `ORD2STAT`：实测诊断状态。`UNDETERMINED` 表示现有数据无法给出可靠起点。
- `ORD2ONST`：只有检测到符合物理方向、连续且超过质量门限的信号时才写入；否则不存在。

诊断比较热标准星与冷标准星各自的“观测光谱 / 模板光谱”响应曲线。二级蓝光若在红端叠加，蓝端相对更亮的热星应表现为正向超额。反向变化会被拒绝，而不会被误报为污染起点。

当前 2026-02-18 数据的命令如下：

```powershell
.\.venv\Scripts\uvex-reduce.exe order2-test `
  --standard Regulus .\output\Regulus\2026-02-18\final\spectrum.fits "<local-isis-template-directory>\p_b8v.dat" `
  --standard Castor .\output\Castor\2026-02-18\final\spectrum.fits "<local-isis-template-directory>\p_a0v.dat" `
  --standard Procyon .\output\Procyon\2026-02-18\final\spectrum.fits "<local-isis-template-directory>\p_f5iv.dat" `
  --hot Regulus --hot Castor --cool Procyon `
  --warning-start 6800 `
  --output-dir .\output\_internal\quality\order2-study-20260218\assessment
```

将诊断结论写入最终科学光谱：

```powershell
.\.venv\Scripts\uvex-reduce.exe postprocess `
  --standard-fits <标准星光谱.fits> `
  --science-fits <科学目标光谱.fits> `
  --template <标准星模板.dat> `
  --output-dir <输出目录> `
  --second-order-assessment .\output\_internal\quality\order2-study-20260218\assessment\second_order_assessment.json
```

## 如何获得可信的实际起点

现有标准星不是严格成对观测，气团、透明度、视宁度和色差引起的狭缝损失会与二级光谱混叠。可靠实验应在光学设置完全不变时进行：

1. 选择蓝端很亮的热标准星，连续拍摄多帧。
2. 加入已知透过曲线的长波通/order-sorting 滤镜，再连续拍摄相同曝光。
3. 两组都做相同 bias、flat、配准和提取；不要独立归一化掉红端差异。
4. 计算无滤镜与有滤镜之差，并用重复帧估计不确定度。
5. 把连续至少一个分辨元、超过 3σ 的正向差异起点报告为实测候选；换另一颗热标准星复核后再作为正式阈值。

没有配对滤镜数据时，软件只报告“未确定”或“候选”，不会自动把候选值升级为确定截止点。
