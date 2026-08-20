# ATR585M Offset 基线与首轮校准库处置（2026-08-16）

## 固定配置

- 相机：`ATR585M (<usb-instance>)`
- N.I.N.A. DeviceId：`ToupTek_\\?\usb#vid_0547&pid_157c#<usb-instance>`
- 增益 / 偏置：100 / 256
- 合并像素：1×1
- 读出模式：1，High Conversion Gain
- 目标温度：−10 °C（直接设定，不使用温度斜坡）
- 新库目录：`%USERPROFILE%\Documents\UVEX-ADV Calibration Library\ATR585M_<usb_instance>\G100_O256\B1x1\R1_High_Conversion_Gain\T-10C`

## Offset 选择

ATR585M 在 gain 100、HCG、offset 16 时，首轮 Bias 有约 86% 的像素被截为零，
黑位明显不足；这不是“传感器没有读噪声”，而是 ADC 下限裁剪。相同相机在
ToupSky/SharpCap 的 offset 256 设置下保留了正常的正偏置基线。因此本机统一把
ATR585M 的采集默认值设为 256。

Offset 是校准兼容性的一部分。O16 的 Bias/Dark 不能校准 O256 科学帧，O256
校准帧也不能用于历史 O16 科学帧；混用会引入错误的基线和暗电流扣除。

## 旧 O16 数据处置

- 旧 `G100_O16` 树中的 Bias、Master Bias、300 秒 Dark、600 秒 Dark 和会话清单均已删除。
- 旧修复脚本也已移除，避免误把已判废的 O16 素材重新建回校准库。
- 这些文件不应作为生产校准帧，也不应与新的 O256 数据合并。

## 新库采集要求

1. Bias、Dark、Flat 与 Science 都固定使用 gain 100、offset 256、1×1、HCG；改变任一项都必须建立独立库。
2. 300 秒和 600 秒 Dark 各至少 5 张，生产库建议 16–32 张；达到 5 张后软件才生成剔除单个最高/最低像素的 master。
3. Master Dark 保留 bias 基线并标记 `DARKBIAS = T`。后期若直接使用它，不得再重复扣除 Master Bias。
4. 新 Bias 完成后必须检查中位数、零值比例和饱和比例；若仍明显截零，停止生成 master 并重新核查驱动实际 Black Level。
5. 每次运行仍必须确认镜盖和屋顶关闭；相机没有机械快门。
