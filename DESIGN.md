---
name: 课堂字幕
description: 深色双语课堂字幕工作区的已实现视觉系统
colors:
  sea-glass: "#9BD6CA"
  paper: "#101D24"
  surface: "#1B2C35"
  control-rail: "#101D24"
  reading-surface: "#101D24"
  raised-surface: "#263D46"
  button-surface: "#2A4049"
  field-surface: "#12222A"
  combo-surface: "#162830"
  line: "#354A53"
  control-border: "#405861"
  field-border: "#3A535C"
  ink: "#EEF4F2"
  muted: "#ADC0C1"
  accent-ink: "#112725"
  original-ink: "#B8CBC9"
  translation-ink: "#F2F5F2"
  draft-amber: "#E6C58D"
  stop-surface: "#33464B"
  stop-ink: "#EEF4F2"
  overlay-surface: "#111827"
  overlay-russian: "#FFD98A"
  overlay-chinese: "#FFFFFF"
typography:
  headline:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
    fontSize: "25px"
    fontWeight: 600
  title:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
    fontSize: "20px"
    fontWeight: 600
  body:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
    fontSize: "13px"
    fontWeight: 400
  label:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
    fontSize: "12px"
    fontWeight: 400
  caption-original:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
    fontSize: "18px"
    fontWeight: 400
    lineHeight: "29px"
  caption-translation:
    fontFamily: "Noto Sans SC"
    fontSize: "32px"
    fontWeight: 400
    lineHeight: "49px"
rounded:
  small: "7px"
  control: "9px"
  panel: "12px"
  window: "14px"
  overlay: "18px"
spacing:
  tight: "8px"
  field: "12px"
  panel: "18px"
  section: "20px"
  reading: "28px"
components:
  button-primary:
    backgroundColor: "{colors.sea-glass}"
    textColor: "{colors.accent-ink}"
    rounded: "{rounded.control}"
    padding: "12px 20px"
  button-secondary:
    backgroundColor: "{colors.button-surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "10px 15px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "8px 10px"
  button-stop:
    backgroundColor: "{colors.stop-surface}"
    textColor: "{colors.stop-ink}"
    rounded: "{rounded.control}"
  field:
    backgroundColor: "{colors.field-surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "10px 12px"
  combo:
    backgroundColor: "{colors.combo-surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "10px 12px"
  card:
    backgroundColor: "{colors.surface}"
    rounded: "{rounded.panel}"
    padding: "18px"
  reading-area:
    backgroundColor: "{colors.reading-surface}"
    rounded: "{rounded.panel}"
  overlay:
    backgroundColor: "{colors.overlay-surface}"
    rounded: "{rounded.overlay}"
    padding: "10px 27px 20px"
---

# Design System: 课堂字幕

## Overview

**Creative North Star: "Bilingual reading workspace"**

深蓝灰色窗口和有层次的暗色面板为双语字幕让出空间。控制紧凑、可辨，界面用克制的海玻璃绿强调选择与主要操作；中文译文在阅读层级中最醒目。

本文件记录当前 WPF 实现。数值中的 px 对应 WPF 的设备无关单位；侧车的 HTML/CSS 是可视化近似，生成的色阶仅供浏览，不是应用实际使用的色板。实际行为与样式以 XAML 和代码为准。

**Key Characteristics:**
- 双语先后排列，原文较轻，译文更大。
- 主窗口控制区与阅读区共用窗口底色，靠留白组织；边框集中在可交互控件。
- 状态与动作有文字说明，选中和键盘焦点有明确视觉变化。

## Colors

### Primary
- **Sea glass:** 主操作、活动状态、选择和键盘焦点；深色字用于浅色按钮上。

### Secondary
- **Draft amber:** 记录里的暂译、中断和识别中标签；悬浮字幕默认俄语另用浅金色。
- **Stop slate:** 课堂进行时的结束按钮使用灰蓝底与浅色文字。

### Neutral
- **Paper / control rail / reading surface:** 控制区与阅读区使用透明底色，共用窗口深蓝灰底色，不绘制满高卡片或阅读区外框。
- **Surface / raised surface:** 设置卡片和弹出层更亮；字段、边线和常规按钮使用各自的深色或灰蓝色。
- **Ink / muted / caption inks:** 浅色正文、弱化标签和较淡的俄语原文共同托起更亮的中文译文。悬浮字幕使用独立的近黑底色与可配置文字颜色。

**The State Label Rule.** 用文字传达课堂或字幕状态，颜色与状态点作为辅助。

## Typography

窗口默认使用 Microsoft YaHei UI，后接 Segoe UI，正文为 13 设备无关单位。常规标签与辅助说明为 12，部分字幕标签为 11。设置页标题为 20 半粗体；准备状态标题为 25 半粗体。

**The Paired Reading Rule.** 主窗口以 18 / 29 的俄语原文与 32 / 49 的中文译文建立层级；两组之间以 24 DIP 留白分隔。这里的数字依次是字号和行高。

## Layout

主窗口改为紧凑控制面板，默认与最小尺寸均为420 × 460 DIP。标题栏高60，状态行高48；状态、计时与字幕/记录开关在同一行。主体左右留白24，麦克风选择和输入音量横向铺满，降噪在其下；底部主操作铺满，「演示／更多」等宽并排，运行时「更多」独占第二行。移除旧侧栏与空阅读画布，不为已删除的主窗口字幕保留布局。更多工具向按钮上方弹出。

打开记录时，普通窗口默认扩展到800 × 560 DIP，控制区保持约372宽，记录区在右侧展开，间隔20；尺寸与位置限制在当前屏幕工作区。关闭记录恢复此前紧凑窗口的尺寸与位置，最大化状态不被强制取消。手动将展开窗口缩窄、主体不足688时，仅显示完整记录区，工具栏仍可关闭记录并返回控制面板。记录中的俄文13/21、中文17/28，均自动换行；中文使用内嵌Noto Sans SC。设置窗口默认620 × 700，字幕外观窗口默认470 × 650；窗口高度受工作区约束，内容可滚动。以上为代码设定和离屏截图所示，不代表所有DPI与显示器条件均已验证。

## Elevation & Depth

主窗口通过留白组织区域，不为整块控制区与阅读区叠加卡片。记录面板以略亮的底色区分，无投影；打开时在原位约 160ms 淡入，关闭立即归还阅读空间。弹出菜单与下拉列表使用更亮的表面。系统关闭客户端动画时跳过这些动画。运行状态点也只在系统允许动画时呼吸变化。

## Shapes

窗口、卡片和控件均为圆角矩形：窗口 14、卡片 12、按钮与输入框 9、分段选项 7。悬浮字幕文字底色圆角为 4；细线与分隔线通常为 1。图标主要是圆头、圆角连接的线条，常规尺寸 18，线宽 1.7。

## Components

### Buttons and controls

「悬浮字幕」与「记录」使用有开关语义的 ToggleButton：关闭时透明，开启时为 #203A3D 底色、#42635F 边框与海玻璃绿文字图标；键盘焦点有明确边框。悬浮字幕按钮随窗口实际显示/隐藏状态同步；记录按钮随记录区开合更新，不添加额外常驻状态文字。

麦克风选择显示去掉括号内驱动说明的短名，完整名称保留在悬停提示及辅助功能名称中；同名设备补充型号/接口信息，仍重名时按设备 ID 生成稳定编号。选择、可用状态与保存标识保持设备原始 ID，不按短名匹配。

主要按钮为海玻璃绿底和深色半粗体；普通按钮为灰蓝底，幽灵按钮透明。悬停和按下分别叠加 7% 与 14% 白色，键盘焦点用海玻璃绿边框，禁用时降低透明度。开始按钮至少高 46；开始后替换为结束按钮。文本框至少高 44，获得键盘焦点时边框变为海玻璃绿且底色稍亮；下拉框与日期选择沿用相近形状。分段控制与设置标签通过底色、文字亮度和字重标识选中项，焦点有边框。复选框、滑块、展开项、滚动条和日历使用同一暗色体系。

### Bilingual captions and history

实时字幕为连续阅读范围，不再按整页清屏。视窗只跟随已开始出现的字形，不提前滚到尚在排队的句尾；已有文字保留，新行出现后约280ms平滑上移，顶部旧行整行渐隐。字号、行高与语言间距见下节。共同字形保持可见，补字、删字、局部替换约220ms同步过渡；替换起点限定在邻接不变文字界定的区间。长字幕滑动时沿用内部共同字形，不因比较长度限制而把整段当作新句；未显示的字形在每次修订后按阅读顺序接续，避免反复延长队尾。大幅语序调整与低相似度整句重译交叉淡化，避免零散公共字跨句飞动。改译等待期间保留上一版，临时回退到已译前缀不删除旧尾句；新版本在当前动画姿态上接续。字形按DPI缓存，帧间只修改保留图层变换和透明度。显示缓冲最多12片段；悬浮字幕随屏幕宽度和字号增加预算以容纳三行（上限俄文6000／中文4000字素），片段连续拼接并按实际宽度自动换行；完整原文译文仍保存在本地记录中。历史记录后台读取、按原文译文条目展示，空记录禁用导出。

### Overlay and appearance

托盘右键菜单依据翻译运行状态提供「开始翻译 / 停止翻译」，不恢复主窗口。字幕与球均取消右键菜单：双击字幕暂停采集与同传并收起，双击球展开并继续，拖动球需超过系统拖动阈值，避免第一次点击阻断双击。字幕的原生标题栏双击与锁定状态下的客户区双击均处理。

字幕与球的连续转场由单一进度驱动，在位置和尺寸固定的临时窗口内完成；交互球窗口始终为48 DIP，动画中不修改可见原生窗口的边界。文字画面等比例缩小并收圆，约0.64–1.0秒内沿平滑轨迹聚成球并滚动贴边；展开沿相同曲线逆向运行，移动、缩放和透明度在所有阶段边界连续。转场只缓存实际可见字幕的透明像素范围，不读取桌面。两端先准备好目标窗口，再在相同屏幕坐标、形状和画面间接续，避免窗口重排导致瞬移；拖动后贴边也使用固定转场层。

收起时冻结字幕字形、逐字进度与图像；连接收尾不会改写这张画面。展开优先使用同一缓存，直到原字幕窗口接续完成才恢复字形动画并追加新结果。暂停会停止麦克风和连接，但保留课堂记录、累计时长和阅读内容；正式翻译重新连接时为新片段分配不冲突的标识，显示旧前缀后接续新内容。演示保留原引擎与词句游标。恢复网络连接与展开动画并行，失败时回收为球并保留文字。转场期间禁用重复启停，退出时取消动画；球的位置不覆盖原字幕保存位置。字幕逐字显示与字幕球转场始终使用程序动画，不依赖 Windows 的 ClientAreaAnimation 开关；状态点、设置抽屉等装饰动效仍遵循系统设置。

置顶悬浮字幕默认覆盖当前显示器的完整宽度，俄文与中文各最多三行，按字号计算高度（默认约312 DIP），保留最小380 × 150；范围无可见外框、整块底板、状态栏与缩放柄，仅文字各行显示可调半透明底色。短行按实际文字长度收紧，各语言多行底色按完整行高连续连接，圆角4、水平留白10；行形状先合成、整组再应用透明度，避免重叠黑线。两种语言之间约6 DIP可见间距；底色范围跟随原有字形过渡，逐帧仅更新保留几何。默认俄语22、中文40，俄语行高为字号1.6倍，中文为1.5倍。中文使用内嵌 Noto Sans SC Regular，原文为常规字重。外观页仅保留黑底白字/白底黑字配对选择、两种字号、底色透明度与交互选项，不再启动屏幕背景采样或截图排除。空白像素保持零alpha，原生拖动仅命中当前可见文字底色块；底色块外围、两种语言之间的空白及滚动视口外不命中，不保留隐形缩放边缘。底色透明度默认25%，可调0%至100%，仅改变底色图层、不改变字形透明度；100%时底色不作为拖动目标。调整即时应用并本地保存；字幕达到范围后连续渐隐上移，滚动条不常驻显示，完整文本可在记录中查看。水平方向吸附当前屏幕边界，竖直位置可拖动；跨屏及DPI变化重新适配，旧宽高配置在运行时升级为当前布局。固定位置与点击下方课件等交互保持原有配置兼容。

### Feedback and settings

底部提示条按需显示短消息，设置页用卡片、标签和折叠区组织字段；费用摘要与账户余额变化以各自文字说明，示例与真实会话保持区分。密钥字段直接显示，并在旁提示本机明文保存。

## Do's and Don'ts

### Do:
- Do 保持俄语原文与中文译文的先后和大小层级。
- Do 保留可见的状态文字、键盘焦点和长内容滚动提示。
- Do 用色调和细边框区分深色面板及控件。
- Do 明确区分演示、估算、余额变化和账单的文字含义。

### Don't:
- Don't 把状态只交给颜色或动画传达。
- Don't 将暂译或等待译文显示成已经确认的译文。
- Don't 添加装饰性光晕或仿仪表装饰。
