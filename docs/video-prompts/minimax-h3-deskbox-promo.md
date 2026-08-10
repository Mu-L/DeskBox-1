---
title: "DeskBox MiniMax H3 商业宣传片提示词：Logo 与胶囊 Hero 镜头版"
description: "以 DeskBox 的 Logo、胶囊模式、按钮微动效、文件格子和格子组合为核心，使用 MiniMax H3 生成一支具有商业软件质感的宣传片。"
keywords:
  - MiniMax H3 视频提示词
  - DeskBox 胶囊模式宣传片
  - 软件产品 Hero Shot
  - Windows 桌面宣传片
  - 文件格子动画
updated: "2026-08-04"
---

# DeskBox MiniMax H3 商业宣传片提示词：Logo 与胶囊 Hero 镜头版

## 这一次的主角应该是胶囊模式

上一版的方向更像“产品功能介绍”：桌面变整齐、文件进入格子、格子之间切换。这个方向是对的，但还不够像商业软件宣传片，也没有把 DeskBox 最有辨识度的视觉能力拍出来。

这次建议把整支片子的叙事改成：

> Logo 华丽出现 → 镜头斜向扫过右上角的胶囊 → 胶囊从收起状态展开成完整格子 → 鼠标经过时按钮和边缘光晕依次出现 → 镜头带出文件格子和格子组合 → DeskBox 品牌定格。

胶囊模式要承担“英雄镜头”，文件整理和格子组合负责证明产品不是一层漂亮皮肤，而是真正可以工作的桌面工具。

胶囊的关键不是简单缩小窗口，而是“收起时保持入口，展开时回到完整工作面”。宣传片需要让观众在几秒内看懂这个变化，同时感受到动画的速度、层次和空间感。

## 参考素材编号

建议尽量上传真实 DeskBox 截图和录屏。H3 负责镜头、光线、空间过渡和轻量动效；真实 UI 负责按钮、文字、图标和布局准确性。

| 编号 | 建议素材 | 用途 |
| --- | --- | --- |
| Picture 1 | DeskBox Logo 高清透明图，包含两个蓝色叠放的圆角块和 DeskBox 字标 | Logo 开场首帧 |
| Picture 2 | 胶囊收起状态的真实桌面截图，最好位于屏幕右上角 | Hero 镜头起始状态 |
| Picture 3 | 同一个胶囊展开后的真实截图 | 胶囊展开目标状态 |
| Picture 4 | 胶囊标题栏、右侧按钮和边缘材质的近距离截图 | 按钮和弥散光晕特写 |
| Picture 5 | 文件格子、待办或随记格子的真实截图 | 产品功能证明 |
| Picture 6 | DeskBox 完整桌面或品牌 Hero 图 | 最后定格 |
| Video 1 | 胶囊从收起到展开的真实录屏，最好包含悬停和按钮出现 | 主运动参考，最重要 |
| Video 2 | 文件拖入格子、格子合并、标题栏切换的真实录屏 | 后半段交互参考 |
| Video 3（可选） | 现有商业软件宣传片或你喜欢的镜头节奏 | 只参考镜头语言，不复制品牌和 UI |
| Audio 1（可选） | 品牌音乐或音效参考 | 只参考节奏、音色和动态范围 |

如果只能准备少量素材，优先级是：`Picture 1`、`Picture 2`、`Picture 3`、`Picture 6`、`Video 1`。胶囊的真实收起/展开录屏，比再上传几张普通设置页面更有价值。

## 推荐生成方向

- 画幅：16:9，适合官网、公众号文章和视频号横屏宣传。
- 时长：12–15 秒。如果当前 H3 的单次时长有限，优先单独生成 Logo/胶囊 Hero，再把文件格子和结尾作为第二段剪入。
- 视觉：Premium Windows productivity software commercial；Windows Fluent、Mica/Acrylic、深色桌面、蓝青色边缘光、少量紫色反射。
- Logo：用真实 Logo 作为核心，不要重新设计图标。允许玻璃、光线、景深和轻微 2.5D 层次，但最终字标必须清楚。
- 胶囊：展开必须从胶囊自身的角点和边缘生长，保持锚点，不要瞬移、弹飞或从屏幕中央重新生成。
- 光晕：使用沿边缘扩散的 soft diffused bloom、thin cyan rim light、subtle violet reflection；不要全屏霓虹、闪电或粒子爆炸。
- 按钮：按钮出现是悬停反馈，不是所有按钮同时弹出。应有明确的鼠标进入、轻微高亮、按钮依次出现和光晕呼吸。
- 文字：尽量只让 `DeskBox` 清晰生成。中文宣传字幕在后期叠加，不把密集中文 UI 交给模型绘制。
- 声音：没有口播和歌词；用低频启动、玻璃材质的 Logo 过渡声、胶囊展开的空气感 whoosh、按钮微弱点击和一个干净的品牌落点。

## 可直接复制给 MiniMax H3 的 Prompt

上传 `Picture 1` 作为开场、`Picture 6` 作为结尾时，保留第一行。如果不使用明确的首尾帧，删除第一行即可；其余结构保留。

```text
How the reference pictures align with the target video — Picture 1 (from [Shot 1]) aligns with the 0.00-second mark of the target video; Picture 6 (from [Shot 5]) aligns with the 15.00-second mark of the target video.

subject_definitions:
<Subject 1> is the authentic DeskBox logo from <Picture 1>: two overlapping rounded blue panels with a clean DeskBox wordmark. Preserve its exact silhouette, relative proportions, blue and cyan color relationships, and recognizable brand identity.
<Subject 2> is the authentic DeskBox capsule in its collapsed state from <Picture 2> and <Video 1>, positioned near the upper-right area of the Windows desktop. It is a compact rounded title surface that keeps its icon, title, key information, and a restrained action area visible.
<Subject 3> is the authentic expanded DeskBox widget from <Picture 3> and <Video 1>. It grows from the capsule's own anchored corner into a complete rounded widget surface while keeping its position, edge relationship, dark translucent material, title bar, and internal content layout.
<Subject 4> is the authentic capsule title-bar interaction from <Picture 4> and <Video 1>: the pointer enters the title area, action buttons appear with a subtle hover response, the edge gains a soft diffused glow, and the interaction settles without changing the product layout.
<Subject 5> is the authentic DeskBox file-grid and grouped-widget system from <Picture 5> and <Video 2>, including file cards, folder cards, Todo or Quick Capture surfaces, title bars, and the member selector used to switch between grouped members.
<Subject 6> is the complete DeskBox brand desktop from <Picture 6>, with a dark near-black and deep navy Windows desktop, translucent Mica or Acrylic surfaces, electric blue and cyan accents, restrained violet reflections, rounded corners, and a calm premium composition.
<Picture 1> is the first-frame reference for [Shot 1], showing the authentic DeskBox logo.
<Picture 2> is the collapsed-capsule reference for [Shot 2], showing the capsule anchored near the upper-right area of the real Windows desktop.
<Picture 3> is the expanded-capsule reference for [Shot 2] and [Shot 3], showing the exact expanded geometry and content surface.
<Picture 4> is the close-up reference for [Shot 3], showing the title bar, action buttons, pointer interaction area, and edge material.
<Picture 5> is the product proof reference for [Shot 4], showing real DeskBox file grids and grouped widget surfaces.
<Picture 6> is the final-frame reference for [Shot 5], showing the complete DeskBox desktop and brand composition.
<Video 1> is a real DeskBox screen recording of the capsule moving from collapsed to expanded, including hover response and action-button appearance. Use it as the source of truth for motion timing, anchor behavior, and UI continuity; do not copy it frame by frame.
<Video 2> is a real DeskBox screen recording of file organization, file dragging, widget grouping, or title-bar member switching. Use it as the source of truth for the meaning and direction of product interactions.
<Video 3> is an optional commercial software video used only as a reference for hero-shot pacing, diagonal camera sweeps, light transitions, and premium advertising rhythm. Do not copy its brand, UI, text, or objects.
<Audio 1> is an optional audio reference for a restrained premium electronic score and polished interface sound design. Use it only as a reference for tempo, dynamics, and sonic texture; do not copy lyrics or add vocals.

summary:
[reference generation + keyframe completion] The target video is a premium commercial for DeskBox, a Windows desktop organization tool whose visual hero is its capsule mode. The film opens with a refined DeskBox logo transition, then performs a strong diagonal hero sweep from the upper-right corner of a real Windows desktop as a collapsed capsule expands into a complete widget. Hovering over the title area reveals subtle action buttons and diffused edge light. The later shots briefly prove that the same desktop also organizes real files into file grids and combines multiple widgets into one switchable work surface. The authentic DeskBox screenshots and recordings are the visual source of truth. The result must feel like a high-end commercial software launch film, not a generic futuristic interface demo.

retention_analysis:
<Subject 1> (appears in [Shot 1]): fully_preserved - retain the exact DeskBox logo silhouette, overlapping blue panels, wordmark proportions, and brand colors.
<Subject 2> (appears in [Shot 2]): fully_preserved - retain the authentic collapsed capsule, its upper-right position, title identity, compact information, rounded edge, and relation to the Windows desktop.
<Subject 3> (appears in [Shot 2], [Shot 3]): fully_preserved - retain the authentic expansion direction, anchored corner, widget geometry, title bar, translucent surface, and content layout.
<Subject 4> (appears in [Shot 3]): fully_preserved - retain the meaning and order of pointer hover, action-button appearance, edge glow, and subtle button feedback.
<Subject 5> (appears in [Shot 4], [Shot 5]): partially_preserved - retain the real file-grid, grouped-widget, and member-switching interfaces while showing only the minimum readable content needed for the product proof.
<Subject 6> (appears in [Shot 5]): fully_preserved - retain the deep navy and near-black desktop, Mica/Acrylic material, electric blue and cyan accents, restrained violet reflections, and calm DeskBox composition.
<Video 1> (capsule motion and UI continuity): fully_preserved - use it as the primary motion reference for the collapsed-to-expanded transition and hover response, not as a literal editing source.
<Video 2> (file and group interaction): fully_preserved - retain the real intention and direction of dragging, grouping, and member switching without replacing them with particles.
<Video 3> (optional cinematic reference): weak_reference - retain only premium pacing, diagonal camera language, and controlled light transitions.
<Audio 1>: reference - use only its tempo, dynamics, and sonic texture if supplied; do not copy lyrics or add vocals.

detailed_description:
The target video is a cinematic, premium Windows software commercial with a strong product-launch opening and one dominant capsule-mode hero shot. The visual language grows from the authentic DeskBox interface: deep near-black and navy backgrounds, translucent Mica/Acrylic surfaces, rounded Windows Fluent cards, bright but controlled electric blue and cyan edge light, and restrained violet reflections. The commercial uses precise 2.5D parallax, shallow depth of field only on foreground light layers, elegant diagonal camera movement, soft volumetric light, and short high-impact transitions. The real DeskBox UI is a locked visual asset: do not redraw, stretch, blur, replace, or invent controls. Do not add a person, office scene, cyberpunk city, random application icons, fake statistics, unreadable dense text, or unrelated futuristic hardware.

[Shot 1] At the 0.00-second opening, begin from <Picture 1>, the authentic DeskBox logo centered against a nearly black deep-navy field. The two overlapping rounded blue panels separate by a few pixels in layered 2.5D space, catching a narrow cyan rim light. The camera performs a small Arc Shot around the logo at fast but controlled speed as a soft blue light passes across the panel edges. The panels align with a precise, satisfying glass-like settle; the DeskBox wordmark resolves sharply beneath them. Add a restrained bloom and a short luminous trail, but keep the logo silhouette exact and immediately recognizable. Hold the finished logo briefly before the next cut. No random particles, no galaxy, no spinning abstract symbol, and no replacement logo.

[Shot 2] At 00:02.200, the shot cuts to the real Windows desktop containing <Subject 2>, the collapsed DeskBox capsule anchored near the upper-right corner. The camera begins close to the upper-right edge and performs a strong diagonal Tracking Shot toward the lower-left with large amplitude at fast, elegant speed, as if the lens is sweeping across the desktop surface. As the camera passes the capsule, the capsule expands from its compact rounded form into <Subject 3>, growing outward from its own anchored corner. The upper-right anchor remains stable; the capsule does not teleport, rotate as a whole, or reappear at the center. The expanded surface reveals the real widget content with a smooth, physically believable unfolding motion, a soft cyan edge bloom, and a faint violet reflection along the outer rim. Use the actual collapsed-to-expanded motion from <Video 1> as the source of truth.

[Shot 3] At 00:05.800, the shot cuts to a close three-quarter macro view based on <Picture 4>. The pointer enters the real title-area interaction zone from <Subject 4>. First, the capsule edge brightens with a thin diffused cyan rim; then the relevant action buttons appear one by one in a restrained 100–160 millisecond micro-animation. Each button gains a soft translucent background, a small focused highlight, and a short edge glow that fades naturally. The buttons must feel like precise Windows interface feedback, not neon stickers. The pointer pauses on one action, the button responds with a quiet light pulse and a delicate click, and the expanded widget remains stable. Keep the title bar, icon, text spacing, and button positions faithful to the reference. Do not make every control glow at once; the interaction must have a clear cause and effect.

[Shot 4] At 00:08.800, the shot transitions with a controlled light sweep into a wider product demonstration. The expanded capsule contracts smoothly back to a compact surface, then the camera trucks left across real DeskBox file grids from <Subject 5>. A small group of real files moves into a matching file card and settles with precise magnetic alignment. Two real widget surfaces align into a compact group, and the title-bar member selector changes the visible member from files to Todo or Quick Capture with a fixed-window crossfade. The desktop position, window size, and surface geometry remain stable. This is a fast proof of product depth, not a second hero effect: one file movement, one group alignment, one member switch, with no particle explosion and no simultaneous transformations.

[Shot 5] At 00:11.500, the shot cuts to the final hero composition based on <Picture 6>. The camera pulls out with small amplitude at slow speed, revealing the complete DeskBox desktop: a calm arrangement of file grids, one expanded widget, and a few compact capsules that keep important information visible without filling the screen. A soft blue-cyan light travels once along the lower desktop plane and gently fades; a restrained violet reflection remains on the rounded surfaces. The DeskBox logo and wordmark are sharp and stable. Hold the final frame for at least 1.5 seconds. Show only the exact product name "DeskBox" as generated on screen and leave clean negative space for a post-produced Chinese tagline. The final frame must converge to <Picture 6> without changing the logo, UI arrangement, proportions, or color balance.

Throughout the video, preserve the real DeskBox interface and the actual capsule animation as the source of truth. Motion should be high-impact but controlled: no window teleportation, no elastic overshoot that breaks the anchored corner, no random duplicated files, no fake buttons, no full-screen neon wash, no excessive lens flare, no handheld shake, no hard sci-fi holograms, no generic AI technology graphics, and no unmotivated camera rotation. Every shot has one main action: logo formation, capsule expansion, hover-button feedback, file/group proof, or final brand presence.

overall_soundscape: Begin with a low, quiet digital room tone and a refined glass-like logo settle. The diagonal capsule sweep produces a clean air movement and a soft low-frequency pass. The expanded capsule has a subtle elastic surface release without a cartoon bounce; pointer hover and button feedback use small, precise interface clicks and a light crystalline tick. File alignment and group switching use restrained magnetic settles. Avoid typing, notification spam, human speech, and loud mechanical impacts.

non_diegetic_music: A premium electronic product score begins with a low sub-bass swell and sparse glassy tones, then adds a controlled pulse during the diagonal capsule hero sweep. Use a clean rhythmic lift for the capsule expansion and a restrained harmonic rise for the final DeskBox frame. The score should have modern synthesizer pads, short plucked tones, and a clear but not aggressive low end, with no vocals, lyrics, cinematic trailer choir, or overly dramatic drop.
```

## 后期字幕建议

H3 不适合负责大量中文小字。建议只让模型生成清晰的 `DeskBox`，中文宣传字幕在剪辑阶段使用 DeskBox 的真实字体和颜色补上：

1. 00:00–00:02：`DeskBox`
2. 00:02–00:06：`收起时安静，需要时展开`
3. 00:06–00:09：`信息就在桌面边缘`
4. 00:11–00:15：`DeskBox｜把桌面变成工作面`

第二、三条字幕可以根据画面删掉。Logo 和胶囊 Hero 镜头已经承担了足够多的信息，不需要在画面上同时列出文件、待办、随记、天气、音乐和搜索。

## 如果想先单独生成胶囊 Hero 镜头

建议先只上传 `Picture 2`、`Picture 3`、`Picture 4` 和 `Video 1`，生成 5–6 秒的单镜头。可以把下面这段作为简化 Prompt：

```text
Premium Windows productivity software commercial, use the authentic DeskBox capsule UI from the reference images and real screen recording as the visual source of truth. Start with the collapsed capsule anchored near the upper-right corner of a dark Windows desktop. The camera performs a strong diagonal Tracking Shot from the upper-right toward the lower-left with large amplitude at fast, elegant speed. As the camera sweeps past, the capsule expands from its compact rounded state into the exact expanded widget, growing from its own anchored corner without teleporting, rotating, or changing its position. Use a soft diffused cyan rim glow and a restrained violet reflection along the capsule edge. Then the pointer enters the title area; relevant action buttons appear one by one with subtle translucent backgrounds, precise hover highlights, and short edge-bloom feedback. The real title bar, icon, text spacing, button positions, and UI proportions remain unchanged. High-impact but controlled motion, premium Mica/Acrylic material, polished Windows Fluent design, no random particles, no full-screen neon, no fake controls, no unreadable text, no cyberpunk, no handheld shake, no generic futuristic interface.
```

这段单独生成成功后，再把它作为整支宣传片中的 `Video 1`，让 H3 继续参考它的镜头速度和胶囊运动。

## 这类镜头最容易失败的地方

### 胶囊展开变成普通缩放

提示词里一定要写 `growing from its own anchored corner`、`the upper-right anchor remains stable` 和 `without teleporting`。核心不是窗口变大，而是胶囊从自己的锚点展开成工作面。

### 光效变成廉价霓虹

保留 `soft diffused cyan rim glow`、`restrained violet reflection`、`controlled bloom`，避免 `neon explosion`、`energy burst`、`lightning`、`galaxy` 和大量粒子。光应该贴着产品边缘扩散，而不是铺满整个屏幕。

### 按钮一起弹出，没有交互因果

明确写“pointer enters first, then relevant buttons appear one by one”。一个按钮的出现必须由鼠标进入或悬停触发，并且只让被触发的按钮亮起来。

### Logo 被模型重新设计

Logo 必须提供高清透明图，并写 `preserve its exact silhouette`、`no replacement logo`。如果 Logo 仍然变形，建议把 Logo 开场改为真实 Logo 动画或后期合成，H3 只负责后续胶囊 Hero 镜头。

## 这版 Prompt 的核心取舍

MiniMax 官方文档支持文生视频、图生视频、首尾帧生成和主体参考，并建议把主要表现物、场景空间和运动变化写清楚；在全参考模式下，还需要明确素材的保留方式和每个镜头中的作用。[官方视频生成文档](https://platform.minimaxi.com/docs/guides/video-generation) · [官方 Prompt 技巧](https://platform.minimaxi.com/docs/guides/video-prompt)

对 DeskBox 来说，最有价值的不是生成一个泛泛的“未来科技桌面”，而是让用户记住三个真实动作：Logo 形成、胶囊展开、按钮响应。文件格子和格子组放在后半段做产品证明，既能展示功能深度，也不会削弱胶囊的英雄地位。
