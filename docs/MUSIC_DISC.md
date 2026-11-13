# 图案 + 音乐 CD

[English](#english)

普通 CD 图案盘只有一条音轨，它的字节本身就是图案：盘面能看到图，放进 CD 机却只有噪声。本功能把歌曲和图案分开放在盘面的两个环形区域：歌曲是正常的音轨，可以在任何 CD 机里播放；图案仍按原来的方式写在自己的区域里，对比度不变。

## 两种布局

| 布局 | 盘面 | CD 机 |
| --- | --- | --- |
| **歌曲在外圈**（默认） | 图案在中间，歌曲围在外侧 | 从第 1 首歌开始播放；图案藏在第 1 轨的前导区（index 00，即“隐藏音轨”），只有倒退到 0:00 之前才会播放 |
| **歌曲在内圈** | 歌曲在中间，图案是外侧的一圈 | 图案是最后一条音轨，播放时是很响的噪声：听完歌曲后请停止播放 |

居中的标志（例如一个大 logo）适合「歌曲在外圈」；环形的设计（沿外缘的文字）适合「歌曲在内圈」。

## 图案还剩多大

CD 恒线速度刻录，盘面面积与播放时间成正比，所以歌曲越长，图案越小。以 Verbatim 43438 预设（记录区半径 24.8–58.0 mm，79:57）为例：

| 歌曲总长 | 歌曲在外圈：图案范围 | 歌曲在内圈：图案范围 | 图案占盘面 |
| --- | --- | --- | --- |
| 10 分钟 | 24.8–55.0 mm | 31.0–58.0 mm | 87% |
| 20 分钟 | 24.8–51.8 mm | 36.1–58.0 mm | 75% |
| 40 分钟 | 24.8–44.6 mm | 44.6–58.0 mm | 50% |
| 60 分钟 | 24.8–36.1 mm | 51.8–58.0 mm | 25% |

设计页会用蓝色引导圈标出歌曲占用的环带，把图案放在其余区域即可。歌曲区域在盘面上呈现为一圈均匀的中间色调纹理。

## 使用方法

**应用内：**

1. 打开或新建 CD 工程（不要选择随盘文件夹：一张 CD 只能放歌曲或文件之一）。
2. 在设计页「随盘文件」卡片的「歌曲（音乐 CD）」中点「添加歌曲…」。支持 MP3、M4A/AAC、WMA、FLAC 和 WAV：应用用 Windows 自带的 Media Foundation 把它们转换成 CD 格式（44.1 kHz、16 位、立体声），转换后的文件放在应用缓存中。可以调整顺序、移除歌曲，选择歌曲位置，以及是否在歌曲之间留 2 秒静音。
3. 卡片下方显示歌曲总长、图案范围和占盘面比例；歌曲太长或图案太小时会给出提示。
4. 照常调整图案，然后在「刻录」页刻录，或在「导出」页导出 WAV + CUE。刻录确认框会列出所有歌曲。

**命令行：**

```powershell
disc-image-studio cd-generate --input logo.png --output disc.wav --music songs.m3u8 --music-position outer --music-gap 2
```

`--music` 接受一个 CD 格式的 WAV，或一个 `.m3u8`/`.m3u` 播放列表（每行一个 WAV 路径，相对于播放列表；`#EXTINF:秒数,标题` 设置下一首的标题）。命令行只接受 CD 格式的 WAV，其他格式请先转换，例如 `ffmpeg -i song.mp3 -ar 44100 -ac 2 -sample_fmt s16 song.wav`。输出是一个 WAV、一个每首歌一条 TRACK 的 CUE 曲目表（歌曲在外圈时，第 1 轨带 `INDEX 00 00:00:00`），以及记录图案范围的 sidecar。`cd-preview-warp` 接受同样的选项，只绘制图案区域。

## 工作原理

- 图案的每个字节必须留在与纯图案盘完全相同的 LBA 上：哪怕只偏移 1 个扇区（16 mm 轨道），图案内缘会转动约 37°、外缘约 16°，整幅图会扭曲。因此歌曲用静音补齐到整扇区（且每轨至少 4 秒），图案只在自己的扇区范围内生成，字节与纯图案盘相同位置的字节一致。
- 歌曲在外圈时，图案从 LBA 0 开始，后面是 75 扇区（1 秒）的静音保护区，二者一起作为第 1 轨的特殊前导区（IMAPI `AddSpecialPregap`）；刻录机自己仍在前面写入标准的 2 秒静音前导区。第 1 首歌的 index 01 紧接其后。
- 歌曲在内圈时，歌曲从 LBA 0 开始，图案是最后一条音轨，一直到 lead-out。
- 两种布局占用的扇区与纯图案盘完全相同，lead-out 位置不变，原有的容量检查仍然有效。
- 刻录时，应用在写盘前读取 IMAPI 实际安排的每条音轨的起始 LBA 和长度，与计划逐一比较；任何不一致都会在写盘前取消刻录，不浪费光盘。

## 验证情况

- **跨平台回归（Linux）：** 布局计算与限制；歌曲在外圈时，图案字节与纯图案盘相同 LBA 的字节逐字节一致；歌曲在内圈时，过了一个交织跨度（约 108 帧）后逐字节一致；歌曲按原样复制并以静音补齐；CUE 金样、sidecar、播放列表与命令行。原有 13 项金样哈希不变。
- **Windows CI（原生 IMAPI，不使用刻录机）：** 用 Windows 自带的 IMAPI 生成完整 DAO 镜像，按 Q 子码逐扇区核对：两种布局、两种子码格式下，每个内容扇区都在计划的 LBA 上且逐字节一致；音轨号、index 和时间与计划一致（包括特殊前导区的 index 00 倒计时）；lead-out 正确。IMAPI 只接受在添加音轨之前调用 `AddSpecialPregap`，并把它放在自己的 2 秒前导区之后，即从 LBA 0 开始。
- **Media Foundation 转换（Windows CI）：** 48 kHz 单声道 1 kHz 测试音转换为 CD 格式后时长 2.000 秒、过零次数 3999（应为 4000），左右声道一致。
- **未验证：** 没有实盘刻录或在 CD 机上试听；极少数老旧 CD 机对很长的隐藏前导区处理可能不同。

---

## English

A picture-only CD has one audio track whose bytes *are* the picture: you see the image, but a CD player plays noise. This feature puts songs and the picture in two separate rings of the disc. The songs are ordinary audio tracks that play in any CD player, and the picture is written into its own area exactly as before, with the same contrast.

**Two layouts.**

| Layout | On the disc | In a CD player |
| --- | --- | --- |
| **Songs on the outer ring** (default) | Picture in the middle, songs around it | Starts at song 1. The picture sits in track 1's pregap (index 00, "hidden track one audio") and only plays if you rewind past 0:00 |
| **Songs on the inner ring** | Songs in the middle, picture as a ring around them | The picture is the last track and plays as loud noise: stop the player after the songs |

A centred logo suits songs on the outer ring; a ring-shaped design (text around the rim) suits songs on the inner ring.

**Picture area.** CDs are written at constant linear velocity, so area grows linearly with playing time: the longer the songs, the smaller the picture. On the Verbatim 43438 preset (24.8–58.0 mm, 79:57):

| Songs | Songs outer: picture | Songs inner: picture | Picture share |
| --- | --- | --- | --- |
| 10 min | 24.8–55.0 mm | 31.0–58.0 mm | 87 % |
| 20 min | 24.8–51.8 mm | 36.1–58.0 mm | 75 % |
| 40 min | 24.8–44.6 mm | 44.6–58.0 mm | 50 % |
| 60 min | 24.8–36.1 mm | 51.8–58.0 mm | 25 % |

The design page hatches the songs' ring with the blue guide, so you can place the picture in the rest of the disc. The music ring looks like an even mid-tone texture.

**Use (app).**

1. Open or create a CD project. Leave the on-disc folder empty: a CD holds either songs or files.
2. In the design page's "Disc files" card, under "Songs (music CD)", click "Add songs…". MP3, M4A/AAC, WMA, FLAC and WAV are accepted. Windows' own Media Foundation converts them to CD format (44.1 kHz, 16-bit, stereo) in the app cache. Reorder or remove songs, choose where they go, and choose whether to leave 2 seconds of silence between them.
3. The card shows the total length, the picture area and its share of the disc. It warns when the songs are too long or the picture area gets small.
4. Edit the picture as usual, then burn from the Burn page or export WAV + CUE from the Export page. The burn confirmation lists every song.

**Use (CLI).**

```powershell
disc-image-studio cd-generate --input logo.png --output disc.wav --music songs.m3u8 --music-position outer --music-gap 2
```

`--music` takes a CD-format WAV or an `.m3u8`/`.m3u` playlist: one WAV path per line, relative to the playlist, with `#EXTINF:seconds,Title` setting the next title. The CLI accepts CD-format WAVs only; convert other files first, for example `ffmpeg -i song.mp3 -ar 44100 -ac 2 -sample_fmt s16 song.wav`.

The output is one WAV, a CUE sheet with one TRACK per song, and a sidecar that records the picture range. With songs on the outer ring, track 1 has `INDEX 00 00:00:00`. `cd-preview-warp` takes the same options and draws only the picture's area.

**How it works.**
- Every picture byte must keep the exact LBA it has on a picture-only disc. One sector of offset (16 mm of track) rotates the picture's inner edge about 37° and its outer edge about 16°, which twists the whole image. So songs are padded with silence to whole sectors (and to the 4-second track minimum), and the picture is generated only for its own sector range. Its bytes equal those of a picture-only disc at the same positions.
- **Songs on the outer ring.** The picture starts at LBA 0, followed by a 75-sector (1 s) silent guard. Together they form track 1's special pregap (IMAPI `AddSpecialPregap`). The recorder still writes its standard 2-second silent pregap before it, and song 1's index 01 follows.
- **Songs on the inner ring.** The songs start at LBA 0, and the picture is the last track, up to the lead-out.
- Both layouts fill exactly the sectors of a picture-only disc, so the lead-out and the existing capacity checks are unchanged.
- Before writing, the app reads the start LBA and length IMAPI actually gave each track and compares them with the plan. Any difference cancels the burn before the disc is touched.

**Validation.**
- **Cross-platform regression (Linux).** Covers the layout maths and limits, and the WAV/CUE/sidecar, playlist and CLI output.
  - With songs on the outer ring, the picture bytes equal the picture-only disc's bytes at the same LBAs, byte for byte.
  - With songs on the inner ring, they are equal after one interleave span (about 108 frames).
  - Songs are copied verbatim and padded with silence.
  - The 13 existing golden hashes are unchanged.
- **Native IMAPI images on the Windows CI runner (no recorder).** Complete DAO images were built by Windows' own IMAPI and checked sector by sector through the Q subchannel, for both layouts and both subchannel formats.
  - Every content sector sits at its planned LBA, byte for byte.
  - Track numbers, indexes and times follow the plan, including the index 00 countdown of the special pregap.
  - The lead-out is where it should be.
  - IMAPI accepts `AddSpecialPregap` only before the first track is added, and places it after its own 2-second pregap, that is from LBA 0.
- **Media Foundation conversion (Windows CI).** A 48 kHz mono 1 kHz tone converts to CD format with a duration of 2.000 s, 3999 zero crossings (4000 expected) and matching channels.
- **Not verified.** No physical burn and no listening test in a CD player. A few very old players may treat a very long hidden pregap differently.
