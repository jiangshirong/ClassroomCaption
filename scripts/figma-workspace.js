// Editable Figma reference for the native app. Run through the Figma Plugin API.
await figma.loadFontAsync({family:'Noto Sans SC',style:'Regular'});
const ids=[];
const rgb=h=>({r:parseInt(h.slice(1,3),16)/255,g:parseInt(h.slice(3,5),16)/255,b:parseInt(h.slice(5,7),16)/255});
const colors={paper:'#101D24',surface:'#15262E',field:'#12222A',ink:'#EEF4F2',muted:'#ADC0C1',accent:'#9BD6CA',line:'#314750'};
const styles={};
for(const [name,hex] of Object.entries(colors)){const s=figma.createPaintStyle();s.name='课堂字幕/'+name;s.paints=[{type:'SOLID',color:rgb(hex)}];styles[name]=s.id;}
function frame(name,dir,w,h,color){const n=figma.createAutoLayout(dir);ids.push(n.id);n.name=name;n.resize(w,h);n.primaryAxisSizingMode='FIXED';n.counterAxisSizingMode='FIXED';n.fills=[];if(color)n.fillStyleId=styles[color];return n;}
function text(parent,name,value,size,color='ink',width=0){const n=figma.createText();ids.push(n.id);n.name=name;n.fontName={family:'Noto Sans SC',style:'Regular'};n.fontSize=size;n.lineHeight={unit:'PIXELS',value:Math.round(size*1.5)};n.characters=value;n.fillStyleId=styles[color];parent.appendChild(n);if(width){n.textAutoResize='HEIGHT';n.resize(width,n.height);}return n;}
function icon(parent,d){const n=figma.createNodeFromSvg('<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none"><path d="'+d+'" stroke="#ADC0C1" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>');parent.appendChild(n);ids.push(n.id,...n.findAll().map(c=>c.id));return n;}
const board=frame('课堂字幕 · 页面与过渡设计','HORIZONTAL',1680,738,'paper');board.x=64;board.y=64;board.paddingLeft=24;board.paddingTop=24;board.paddingRight=24;board.paddingBottom=24;board.itemSpacing=28;
const app=frame('主窗口 · 运行中','VERTICAL',1000,690,'paper');board.appendChild(app);app.cornerRadius=14;app.paddingLeft=18;app.paddingRight=18;
const header=frame('标题栏','HORIZONTAL',964,60);app.appendChild(header);header.paddingLeft=2;header.paddingRight=2;header.counterAxisAlignItems='CENTER';header.itemSpacing=12;
icon(header,'M5,5 H19 Q21,5 21,7 V17 Q21,19 19,19 H5 Q3,19 3,17 V7 Q3,5 5,5 Z M7 15h4 M15 15h2 M7 11h2 M13 11h4');text(header,'应用名称','课堂字幕',14);
const gap=frame('标题弹性间距','HORIZONTAL',20,20);header.appendChild(gap);gap.layoutSizingHorizontal='FILL';
icon(header,'M10 5H3 M12 19H3 M14 3v4 M16 17v4 M21 12h-9 M21 19h-5 M21 5h-7 M8 10v4 M8 12H3');icon(header,'M5 12h14');icon(header,'M4 4H20V20H4Z');icon(header,'M18 6 6 18 M6 6l12 12');
const body=frame('工作区','HORIZONTAL',964,612);app.appendChild(body);body.itemSpacing=16;
const rail=frame('控制栏','VERTICAL',184,612,'surface');body.appendChild(rail);rail.cornerRadius=12;rail.paddingLeft=14;rail.paddingRight=14;rail.paddingTop=20;rail.paddingBottom=16;rail.itemSpacing=12;
text(rail,'输入设备标签','麦克风',12,'muted');const input=frame('麦克风选择','VERTICAL',156,44,'field');rail.appendChild(input);input.cornerRadius=8;input.paddingLeft=10;input.paddingTop=10;text(input,'设备','麦克风阵列',12,'muted');
const meter=frame('音量','HORIZONTAL',156,3,'line');rail.appendChild(meter);const metered=frame('输入音量','HORIZONTAL',57,3,'accent');meter.appendChild(metered);
const spacer=frame('控制栏留白','VERTICAL',156,20);rail.appendChild(spacer);spacer.layoutSizingVertical='FILL';
// Import and instance in the same call: library-only imports do not have persistent canvas IDs.
const set=await figma.importComponentSetByKeyAsync('cc8b558dc7d9684011b6b99ce8e6509399bc836b');
const source=set.children.find(n=>n.name==='Variant=Primary, State=Default, Size=Medium')??set.children[0];
const button=source.createInstance();rail.appendChild(button);ids.push(button.id);
for(const t of button.findAllWithCriteria({types:['TEXT']})){for(const seg of t.getStyledTextSegments(['fontName']))await figma.loadFontAsync(seg.fontName);t.fontName={family:'Noto Sans SC',style:'Regular'};t.characters='结束课堂';ids.push(t.id);}
button.resize(156,44);button.fills=[{type:'SOLID',color:rgb('#33464B')}];button.cornerRadius=8;
text(rail,'演示入口','演示',12,'muted');text(rail,'更多工具','更多',12,'muted');
const read=frame('双语字幕区','VERTICAL',764,612,'surface');body.appendChild(read);read.cornerRadius=12;read.paddingLeft=28;read.paddingRight=28;read.paddingTop=20;read.itemSpacing=28;
const status=frame('状态与工具','HORIZONTAL',708,32);read.appendChild(status);status.counterAxisAlignItems='CENTER';status.itemSpacing=16;text(status,'状态','收音中',12,'muted');text(status,'计时','12:34',12,'muted');const sx=frame('状态间距','HORIZONTAL',20,10);status.appendChild(sx);sx.layoutSizingHorizontal='FILL';text(status,'悬浮入口','悬浮字幕',12,'muted');text(status,'记录入口','记录',12,'muted');
text(read,'俄语连续阅读','Если центр тяжести поднимается, начальная остойчивость уменьшается. Поэтому необходимо учитывать положение тяжёлого груза.',18,'muted',708);
text(read,'中文连续阅读','重心升高时，船舶的初稳性会减小。因此，需要考虑重物的放置位置。',32,'ink',708);
const story=frame('字幕过渡 · 状态示意','VERTICAL',600,690,'paper');board.appendChild(story);story.itemSpacing=24;
text(story,'过渡设计','连续阅读，不再清屏',22,'ink',570);text(story,'局部改译规则','相同文字保留位置与可见度；补字、删字、替换同时过渡。',14,'muted',570);
for(const [name,value,opacity] of [['旧译','船舶的初稳性会减小，因此需要调整货物位置。',.55],['改译','船舶初稳性减小，因此必须调整货物的位置。',1]]){const box=frame(name,'VERTICAL',570,112,'surface');story.appendChild(box);box.paddingLeft=18;box.paddingRight=18;box.paddingTop=16;box.cornerRadius=10;const t=text(box,name+'内容',value,24,'ink',534);t.opacity=opacity;}
text(story,'滚动规则','顶部旧行渐隐，新行从底部接续；中文和俄文各自保留阅读范围。',14,'muted',570);
const overlay=frame('课件上的字幕','VERTICAL',570,146);story.appendChild(overlay);overlay.itemSpacing=12;
for(const [name,value,size] of [['俄文','Если центр тяжести поднимается…',18],['中文','重心升高时，初稳性会减小。',28]]){const chip=figma.createAutoLayout('HORIZONTAL');ids.push(chip.id);chip.name=name+'半透明底色';chip.fills=[{type:'SOLID',color:{r:0,g:0,b:0},opacity:.75}];chip.paddingLeft=10;chip.paddingRight=10;chip.paddingTop=4;chip.paddingBottom=4;chip.cornerRadius=4;overlay.appendChild(chip);text(chip,name+'文字',value,size);}
const counts={};for(const n of board.findAll())counts[n.type]=(counts[n.type]??0)+1;
return {createdNodeIds:ids,wrapperId:board.id,editableNodeCounts:counts,imageNodes:[],fontFamily:'Noto Sans SC',buttonInstanceId:button.id};
