# CD 对比度测试盘与自定义光盘字节

[English](#english)

CD 图案由两种字节的纹理组成：深色 `0D`（EFM 码字只有 1 个跳变，长坑长岸）和浅色 `AA`（5 个跳变，短坑短岸）。盘面能看到的明暗，来自这两种纹理在刻录层上的差别。本功能做两件事：

1. **信道模型**：按 ECMA-130 模拟刻录机真正写入的内容——延迟交织、CIRC（C2/C1 校验字节并反相）、子码和 EFM 合并位——在完整信道比特流上比较候选字节，而不是只看主机写入的字节。
2. **对比度测试盘**：把模型选出的浅色候选刻成带编号的同心圈，每圈两侧都是标准 `AA`，用肉眼在自己的盘片和光驱上选出最亮的一圈，再把它用于正式刻录。

## 预期

- 模型显示 `0D` 已经是安全范围内最暗的纹理：同样暗的 `20`、`30`、`CD` 会让直流平衡（DSV）持续漂移，可能影响读盘或刻录，因此被安全检查排除；能通过检查的其他深色字节都更亮。测试盘因此**只改变浅色**，深色固定为 `0D`。
- 浅色方面，单个字节最多比 `AA` 多约 2% 跳变；交替的字节对（例如 `F2-A8`）可多 8–10%。CIRC 校验和子码占每帧 33 个符号中的 9 个，会稀释两种纹理的差别，所以模型中的提升是在计入它们之后的数字。
- 模型只预测跳变密度等纹理指标，不预测反射率；实际效果取决于染料、刻录机写入策略与速度。**测试盘才是判断依据**，看不出差别时请保留标准字节。

## 候选集 `cd-contrast-v1`

| 圈号 | 浅色字节 | 模型预测：每毫米跳变数相对 `AA` |
| --- | --- | --- |
| 1 | `F2-A8` | +10.0% |
| 2 | `A8-F6` | +9.9% |
| 3 | `F2-44` | +9.1% |
| 4 | `F2-40` | +8.6%（对合并位同分规则不敏感） |
| 5 | `28-F2` | +8.0% |
| 6 | `B6` | +2.3%（最佳单字节） |

`F2-A8` 表示偶数位置写 `F2`、奇数位置写 `A8`。延迟交织补偿使逻辑字节 i 落在物理数据槽 i（或 i+4），所以这种交替在盘面上同样成立（有测试验证）。每个候选都与 `0D` 一起通过了安全检查。`44-F2`（+10.7%）因效果依赖刻录机的合并位同分选择而未入选；`F7-A8` 与 `0D` 组合时超出安全范围。

## 测试盘布局

从内到外：`0D` 深色参考圈 → `AA` 参考圈 → 候选 1 → `AA` → 候选 2 → … → 候选 6 → `AA` 直到外缘。候选圈宽约 2.5–3 mm（随盘片几何变化），参考圈宽 2 mm。每个参考圈在 12、4、8 点钟方向用 `0D` 写出下一圈的编号（5×7 点阵）。采用宽的同心圈而不是小扇区：一个 C2 码字在轨道上跨越约 18 mm，校验字节只有在较长的均匀区域内才稳定。

## 使用方法

**应用内：**

1. 打开 CD 工程（盘片参数与正式刻录相同，例如 Verbatim CD-R AZO 43438 预设）。
2. 在「刻录」页勾选「改为刻录对比度测试盘（不写入图片）」，选择与正式刻录相同的速度，放入空白盘并刻录。
3. 在明亮、均匀的光源下（白墙、窗户）倾斜盘片观察数据面，找出比两侧 `AA` 圈更亮、更突出的圈号；都看不出差别就保留标准。
4. 在设计页「影调」面板的「浅色字节（CD 音轨）」中选择该圈号，然后照常刻录图片。自定义字节只用于音轨并固定 2 级影调；数据盘始终使用标准字节。

**命令行：**

```powershell
disc-image-studio cd-contrast-test --output contrast.wav --r0 24.837775 --r1 58.020875 --sectors 359848 --usable-sectors 359846
disc-image-studio cd-generate --input logo.png --output logo.wav --palette-light F2-A8
disc-image-studio cd-channel-report --pairs true --output report.json
```

`cd-contrast-test` 写出 WAV/CUE、`.contrast.json`（圈号、半径、模型预测）和 `.legend.png`。`cd-generate` 的 `--palette-dark`／`--palette-light` 会先经过安全检查，超出范围的组合会被拒绝，除非显式指定 `--allow-unvetted true`。所用字节记录在音轨 sidecar 中，读回预览据此还原明暗。

## 模型与验证

代码位于 `src/DiscImageStudio.Cd/Physical/`：EFM 表由 `tools/generate_cd_efm_table.py` 从 ECMA-130 附录 D 生成（pdftotext 与 pdfplumber 两次提取须一致）；CIRC 按附录 C 实现，并用冲激输入逐槽复现图 C.4 的槽位与延迟表；合并位按附录 E（游程 2–10 个零、禁止伪同步、一个符号前视取最小 |DSV|，同分时比较两种顺序）。

独立核对：对纯 `0D`／`AA`／`63` 与随机数据，最大 |DSV| 为 11／11／23，20 kHz 以下功率占比为 0.048%／0.044%／0.558%／0.169%，与 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md) 中独立 Python 链路的 11／11／23 与 0.0476%／0.0445%／0.5561%／0.1696% 一致。

**安全检查**（`CdPatternSafety`）：一组深浅字节只有在其纯色、25/50/75% 抖动混合和 2–96 字节条纹，在两种合并位同分顺序下都不比现有调色板（纯 `10`、纯 `63`、4 级混合、`0D/AA` 各纹理）更差时才被接受（最大 |DSV| 27、216 位窗口偏置 0.130、低频代理 0.122%）。这保证自定义字节不超出已被实盘验证的范围，但不是可读性证明。

**未验证的部分：** 没有实盘刻录或光学测量；不同刻录机的合并位选择、写入策略和速度都可能改变结果。

---

## English

A CD picture is drawn with two byte textures: dark `0D` (one transition per EFM word, long marks and lands) and light `AA` (five transitions, short ones). This feature adds:

1. **A channel model** that simulates what a recorder writes per ECMA-130 — the generator's delay interleave, CIRC (C2/C1 parity, inverted), subcode and EFM merging bits — so candidate bytes are compared on the full channel bit stream instead of on host bytes.
2. **A contrast test disc** that burns the model's light-tone candidates as numbered concentric bands, each between two standard `AA` bands, so you can pick the brightest band for your own discs and drive and use it for real burns.

**Expectations.** The model shows `0D` is already the darkest texture inside the safety envelope (`20`, `30`, `CD` are as dark but let the DC balance drift), so the test varies the light tone only. Single bytes gain at most about 2 % transitions over `AA`; alternating byte pairs such as `F2-A8` gain 8–10 %, after the parity and subcode symbols (9 of every 33) are accounted for. The model predicts texture, not reflectance: the burned test disc decides, and keeping the standard bytes is a valid result.

**Candidate set `cd-contrast-v1`:** band 1 `F2-A8` (+10.0 %), 2 `A8-F6` (+9.9 %), 3 `F2-44` (+9.1 %), 4 `F2-40` (+8.6 %, insensitive to the merging-bit tie-break), 5 `28-F2` (+8.0 %), 6 `B6` (+2.3 %, best single byte). `F2-A8` means `F2` at even and `A8` at odd byte positions; the interleave compensation keeps logical byte i in physical data slot i (or i + 4), so the alternation holds on the disc (tested). Every candidate passes the safety check with `0D`.

**Layout.** From the hub: a `0D` anchor band, then `AA` | candidate 1 | `AA` | … | candidate 6 | `AA` to the rim. The number of the next band is drawn in `0D` inside each `AA` band at 12, 4 and 8 o'clock. Bands are wide on purpose: one C2 codeword spans about 18 mm of track, and parity only settles inside a long uniform region.

**Use (app).** Open a CD project with the same disc settings you burn with. On the Burn page tick "Burn the contrast test disc instead of the picture", use the speed you will use for the real disc, and burn a blank disc. Tilt the shiny side under bright, even light and find the band that stands out against the `AA` bands beside it. Then choose that band under "Light disc bytes (CD audio)" in the Tone panel and burn your picture as usual. Custom bytes are audio-only and use 2 tone levels; data discs keep the standard bytes.

**Use (CLI).** `cd-contrast-test` writes the WAV/CUE, a `.contrast.json` legend (bands, radii, predictions) and a `.legend.png`. `cd-generate --palette-dark/--palette-light` refuses byte pairs outside the safety envelope unless `--allow-unvetted true` is given; the bytes are recorded in the track sidecar so the read-back preview shows the right tones. `cd-channel-report` ranks single bytes (and, with `--pairs true`, byte pairs).

**Validation.** The EFM table is generated from ECMA-130 Annex D (two independent PDF extractions must agree). The CIRC encoder reproduces figure C.4's slot/delay table under impulse input. The chain reproduces the upstream audit's independent figures (max |DSV| 11/11/23 and low-frequency shares 0.048/0.044/0.558 % for pure `0D`/`AA`/`63`, 0.169 % for random data). The safety check accepts a pair only if its pure fills, 25/50/75 % dither mixes and 2–96-byte stripes stay, under both merging tie-break orders, inside the envelope of the shipped palette (max |DSV| 27, 216-bit bias 0.130, low-frequency proxy 0.122 %). Not verified: no physical burn or optical measurement; recorders' merging-bit choices, write strategies and speeds can change the visible result.
