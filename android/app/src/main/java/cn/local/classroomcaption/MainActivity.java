package cn.local.classroomcaption;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.*;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import okhttp3.*;

public final class MainActivity extends Activity {
    private static final int BG=0xff101d24, SURFACE=0xff1b2c35, INK=0xffeef4f2, MUTED=0xffadc0c1, ACCENT=0xff9bd6ca;
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final OkHttpClient net=new OkHttpClient.Builder().connectTimeout(18,TimeUnit.SECONDS).readTimeout(30,TimeUnit.SECONDS).build();
    private final LinkedHashMap<String,String> sentences=new LinkedHashMap<>();
    private final ArrayBlockingQueue<byte[]> audioQueue=new ArrayBlockingQueue<>(100);
    private TextView status, ru, zh, note, cost;
    private EditText aliyun, workspace, terms;
    private LinearLayout settings;
    private Button start, stop;
    private volatile boolean running, taskStarted;
    private volatile long generation;
    private volatile WebSocket socket;
    private volatile AudioRecord microphone;
    private String sessionPath="", asrKey="", contextTerms="";
    private final Map<String,String> translations=new LinkedHashMap<>(), links=new HashMap<>();
    private boolean finishing;
    private boolean missingAudio;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state); getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG); getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        for(String file:fileList())if(file.startsWith("session-")&&file.endsWith(".jsonl")&&file.compareTo(sessionPath)>0)sessionPath=file;
        LinearLayout root=new LinearLayout(this); root.setOrientation(1); root.setPadding(dp(22),dp(20),dp(22),dp(12)); root.setBackgroundColor(BG); setContentView(root);
        LinearLayout header=new LinearLayout(this); header.setGravity(Gravity.CENTER_VERTICAL); root.addView(header,new LinearLayout.LayoutParams(-1,-2));
        TextView title=text("课堂字幕",23,INK,true); header.addView(title,new LinearLayout.LayoutParams(0,-2,1));
        Button gear=button("设置",false); header.addView(gear); gear.setContentDescription("模型设置");
        status=text("准备开始",13,ACCENT,false); status.setPadding(0,dp(8),0,dp(18)); root.addView(status);
        ScrollView scroll=new ScrollView(this); scroll.setFillViewport(true); root.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        LinearLayout body=new LinearLayout(this); body.setOrientation(1); scroll.addView(body);
        LinearLayout card=new LinearLayout(this); card.setOrientation(1); card.setPadding(dp(18),dp(20),dp(18),dp(20)); card.setBackground(bg(SURFACE,20)); body.addView(card,new LinearLayout.LayoutParams(-1,-2));
        TextView rlabel=text("俄语原文",12,MUTED,false); card.addView(rlabel);
        ru=text("开始后显示俄语识别结果",18,0xffb8cbc9,false); ru.setPadding(0,dp(10),0,dp(24)); card.addView(ru);
        TextView clabel=text("中文翻译",12,MUTED,false); card.addView(clabel);
        zh=text("等待翻译",29,INK,true); zh.setPadding(0,dp(10),0,dp(8)); zh.setLineSpacing(dp(5),1); card.addView(zh);
        note=text("手机仅在前台收音，音频不保存。",12,MUTED,false); note.setPadding(dp(2),dp(16),0,dp(12)); body.addView(note);
        cost=text("识别过程中会产生模型用量。",12,MUTED,false); body.addView(cost);
        settings=new LinearLayout(this); settings.setOrientation(1); settings.setPadding(dp(18),dp(18),dp(18),dp(12)); settings.setBackground(bg(SURFACE,20));
        LinearLayout.LayoutParams sp=new LinearLayout.LayoutParams(-1,-2); sp.topMargin=dp(18); body.addView(settings,sp); settings.setVisibility(View.GONE);
        settings.addView(text("模型连接",18,INK,true)); settings.addView(label("阿里云百炼 API Key")); aliyun=input("粘贴阿里云 API Key",false); settings.addView(aliyun);
        settings.addView(label("Workspace ID · 北京地域")); workspace=input("业务空间 ID",false); settings.addView(workspace);
        settings.addView(label("固定译法 · 可留空")); terms=input("例如 остойчивость = 稳性",true); settings.addView(terms);
        settings.addView(text("密钥明文保存在这台手机，请勿将截图或密钥发给他人。",12,MUTED,false));
        Button save=button("保存设置",false); settings.addView(save); save.setOnClickListener(v->{getPreferences(MODE_PRIVATE).edit().putString("aliyun",aliyun.getText().toString().trim()).putString("workspace",workspace.getText().toString().trim()).putString("terms",terms.getText().toString()).apply(); settings.setVisibility(View.GONE); status.setText("设置已保存");});
        Button export=button("分享最近课堂文字",false); settings.addView(export); export.setOnClickListener(v->shareSession());
        String initialAliyun="", initialWorkspace="";
        try(BufferedReader in=new BufferedReader(new InputStreamReader(getAssets().open("initial-keys.json"),StandardCharsets.UTF_8))){JSONObject initial=new JSONObject(in.readLine());initialAliyun=initial.optString("aliyun");initialWorkspace=initial.optString("workspace");}catch(Exception ignored){}
        aliyun.setText(getPreferences(MODE_PRIVATE).getString("aliyun",initialAliyun));
        workspace.setText(getPreferences(MODE_PRIVATE).getString("workspace",initialWorkspace));
        terms.setText(getPreferences(MODE_PRIVATE).getString("terms",""));
        gear.setOnClickListener(v->{settings.setVisibility(settings.getVisibility()==View.VISIBLE?View.GONE:View.VISIBLE); if(settings.getVisibility()==View.VISIBLE) scroll.post(()->scroll.fullScroll(View.FOCUS_DOWN));});
        LinearLayout controls=new LinearLayout(this); controls.setPadding(0,dp(16),0,0); root.addView(controls);
        start=button("开始翻译",true); stop=button("结束课堂",false); controls.addView(start,new LinearLayout.LayoutParams(0,dp(52),1)); LinearLayout.LayoutParams bp=new LinearLayout.LayoutParams(0,dp(52),1); bp.leftMargin=dp(10); controls.addView(stop,bp); stop.setEnabled(false);
        start.setOnClickListener(v->begin()); stop.setOnClickListener(v->end("已结束"));
    }
    private int dp(float n){return (int)(getResources().getDisplayMetrics().density*n+.5f);}
    private GradientDrawable bg(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d;}
    private TextView text(String s,int size,int color,boolean bold){TextView t=new TextView(this);t.setText(s);t.setTextSize(size);t.setTextColor(color);if(bold)t.setTypeface(Typeface.DEFAULT,Typeface.BOLD);return t;}
    private TextView label(String s){TextView t=text(s,12,MUTED,false);t.setPadding(0,dp(18),0,dp(6));return t;}
    private EditText input(String hint,boolean multi){EditText e=new EditText(this);e.setTextColor(INK);e.setHintTextColor(0xff82999b);e.setSingleLine(!multi);e.setInputType(multi?InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_FLAG_MULTI_LINE:InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD|InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);e.setHint(hint);e.setPadding(dp(12),dp(12),dp(12),dp(12));e.setBackground(bg(0xff12222a,10)); if(multi)e.setMinLines(2);return e;}
    private Button button(String s,boolean primary){Button b=new Button(this);b.setText(s);b.setTextSize(14);b.setAllCaps(false);b.setTextColor(primary?0xff112725:INK);b.setBackground(bg(primary?ACCENT:0xff2a4049,14));return b;}
    private void begin(){
        if(running||finishing)return; asrKey=aliyun.getText().toString().trim();contextTerms=terms.getText().toString().trim();
        if(asrKey.isEmpty()||!workspace.getText().toString().trim().matches("[A-Za-z0-9-]+")){settings.setVisibility(View.VISIBLE);status.setText("请填写阿里云 Key 和业务空间 ID");return;}
        try { configuration(); } catch(JSONException e){status.setText("固定译法请按 俄语 = 中文 填写，或留空");return;}
        if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO},7);return;}
        int size=AudioRecord.getMinBufferSize(16000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);
        if(size<=0){status.setText("手机不支持所需的录音格式");return;}
        try { microphone=new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION,16000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT,Math.max(size,6400)); if(microphone.getState()!=AudioRecord.STATE_INITIALIZED)throw new IllegalStateException(); microphone.startRecording(); }
        catch(Exception e){if(microphone!=null)microphone.release();microphone=null;status.setText("无法打开麦克风，请检查权限或其他应用");return;}
        getPreferences(MODE_PRIVATE).edit().putString("aliyun",asrKey).putString("workspace",workspace.getText().toString().trim()).putString("terms",terms.getText().toString()).apply();
        running=true;taskStarted=false;missingAudio=false;generation++;long run=generation;sentences.clear();translations.clear();links.clear();audioQueue.clear();
        sessionPath="session-"+System.currentTimeMillis()+".jsonl";start.setEnabled(false);stop.setEnabled(true);settings.setVisibility(View.GONE);ru.setText("等待识别…");zh.setText("等待翻译…");status.setText("收音中 · 正在连接");
        new Thread(()->capture(run),"audio-capture").start();
        Request request=new Request.Builder().url("wss://"+workspace.getText().toString().trim()+".cn-beijing.maas.aliyuncs.com/api-ws/v1/realtime?model=qwen3.8-livetranslate-flash-realtime").header("Authorization","Bearer "+asrKey).build();
        socket=net.newWebSocket(request,new WebSocketListener(){
            @Override public void onOpen(WebSocket ws,Response response){if(run!=generation){ws.cancel();return;}try{ws.send(configuration());}catch(Exception e){fail(run,"语音任务无法启动");}}
            @Override public void onMessage(WebSocket ws,String message){if(run!=generation)return;try{JSONObject event=new JSONObject(message);ui.post(()->handleEvent(run,event));}catch(JSONException e){fail(run,"同传返回格式异常");}}
            @Override public void onFailure(WebSocket ws,Throwable t,Response response){fail(run,response!=null&&(response.code()==401||response.code()==403)?"语音服务鉴权失败，请检查阿里云 Key":"语音连接中断，请检查网络后重试");}
            @Override public void onClosed(WebSocket ws,int code,String reason){if(running&&run==generation)fail(run,"语音连接已关闭，请重新开始");}
        });
        ui.postDelayed(()->{if(run==generation&&running&&!taskStarted)end("语音连接超时，请检查网络后重试");},20000);

    }
    private String configuration()throws JSONException {
        JSONObject phrases=new JSONObject();
        for(String line:contextTerms.split("\n")){if(line.trim().isEmpty())continue;String[] pair=line.split("[=＝]",2);if(pair.length!=2||pair[0].trim().isEmpty()||pair[1].trim().isEmpty())throw new JSONException("term");phrases.put(pair[0].trim(),pair[1].trim());}
        return new JSONObject().put("type","session.update").put("session",new JSONObject().put("output_modalities",new JSONArray().put("text")).put("audio",new JSONObject().put("input",new JSONObject().put("format",new JSONObject().put("type","pcm").put("sample_rate",16000)))).put("translation",new JSONObject().put("language","zh").put("corpus",new JSONObject().put("phrases",phrases)))).toString();
    }
    private void handleEvent(long run,JSONObject e){
        if(run!=generation)return;String type=e.optString("type"),id=e.optString("item_id");
        if(type.equals("session.updated")&&!taskStarted){taskStarted=true;status.setText("同传收音中");WebSocket ws=socket;new Thread(()->sendAudio(run,ws),"audio-send").start();}
        else if(type.equals("conversation.item.created")){JSONObject item=e.optJSONObject("item");if(item!=null&&item.optString("role").equals("assistant"))links.put(item.optString("id"),e.optString("previous_item_id"));}
        else if(type.equals("conversation.item.input_audio_transcription.delta"))sentences.put(id,sentences.getOrDefault(id,"")+e.optString("delta"));
        else if(type.equals("conversation.item.input_audio_transcription.completed")){sentences.put(id,e.optString("transcript"));write("source_final",e.optString("transcript"),"");}
        else if(type.equals("response.text.delta"))translations.put(id,translations.getOrDefault(id,"")+e.optString("delta"));
        else if(type.equals("response.text.done")){translations.put(id,e.optString("text"));write("translation_draft",sentences.getOrDefault(links.get(id),""),e.optString("text"));}
        else if(type.equals("error")||type.equals("conversation.item.input_audio_transcription.failed")){fail(run,"同传服务拒绝或未能完成请求，请检查模型权限和网络");}
        else if(type.equals("session.finished")){finishing=false;generation++;if(socket!=null)socket.close(1000,"done");socket=null;start.setEnabled(true);stop.setEnabled(false);status.setText("已结束");}
        if(sentences.size()>200||translations.size()>200){fail(run,"字幕缓存已满，请结束并重新开始");return;}
        StringBuilder russian=new StringBuilder(),chinese=new StringBuilder();
        List<String> keys=new ArrayList<>(sentences.keySet());for(String key:keys.subList(Math.max(0,keys.size()-12),keys.size())){russian.append(sentences.get(key)).append(" ");for(Map.Entry<String,String> link:links.entrySet())if(key.equals(link.getValue()))chinese.append(translations.getOrDefault(link.getKey(),""));}
        if(russian.length()>0)ru.setText(russian.toString());if(chinese.length()>0)zh.setText(chinese.toString());
    }
    private void capture(long run){byte[] b=new byte[3200];try{while(running&&generation==run){AudioRecord mic=microphone;if(mic==null)break;int n=mic.read(b,0,b.length);if(n>0){byte[] packet=Arrays.copyOf(b,n);if(!audioQueue.offer(packet)){audioQueue.poll();audioQueue.offer(packet);if(!missingAudio){missingAudio=true;ui.post(()->note.setText("连接过慢，开头部分声音未发送"));}}}else if(n<0){fail(run,"麦克风收音中断");break;}}}catch(Exception e){if(running)fail(run,"麦克风收音中断");}}
    private void sendAudio(long run,WebSocket ws){try{while(generation==run&&(running||!audioQueue.isEmpty())){byte[] p=audioQueue.poll(300,TimeUnit.MILLISECONDS);if(p!=null&&!ws.send(new JSONObject().put("type","input_audio_buffer.append").put("audio",android.util.Base64.encodeToString(p,android.util.Base64.NO_WRAP)).toString())){fail(run,"声音发送失败");return;}}if(generation==run)ws.send(new JSONObject().put("type","session.finish").toString());}catch(Exception e){fail(run,"声音发送失败");}}
    private void write(String type,String original,String translation){try(FileOutputStream out=openFileOutput(sessionPath,MODE_APPEND)){String line=new JSONObject().put("type",type).put("time",System.currentTimeMillis()).put("ru",original).put("zh",translation).toString()+"\n";out.write(line.getBytes(StandardCharsets.UTF_8));}catch(Exception e){note.setText("文字保存失败，请检查手机存储空间");}}
    private void shareSession(){if(sessionPath.isEmpty()){status.setText("还没有本次课堂记录");return;}try(BufferedReader input=new BufferedReader(new InputStreamReader(openFileInput(sessionPath),StandardCharsets.UTF_8))){StringBuilder output=new StringBuilder();String line;while((line=input.readLine())!=null){JSONObject entry=new JSONObject(line);String type=entry.optString("type");output.append(type.equals("asr_final")?"俄语识别：":type.equals("asr_language_anomaly")?"语种异常：":"翻译暂稿：").append(entry.optString("ru")).append('\n');if(!entry.optString("zh").isEmpty())output.append(entry.optString("zh")).append('\n');output.append('\n');}Intent intent=new Intent(Intent.ACTION_SEND);intent.setType("text/plain");intent.putExtra(Intent.EXTRA_TEXT,output.toString());startActivity(Intent.createChooser(intent,"分享课堂文字"));}catch(Exception e){status.setText("无法读取本次课堂文字");}}
    private void fail(long run,String message){ui.post(()->{if(run==generation&&running)end(message);});}
    private void end(String message){if(!running)return;running=false;finishing=true;AudioRecord mic=microphone;microphone=null;if(mic!=null){try{mic.stop();}catch(Exception ignored){}mic.release();}stop.setEnabled(false);status.setText(message+" · 正在收尾");long run=generation;ui.postDelayed(()->{if(finishing&&run==generation){finishing=false;generation++;if(socket!=null)socket.cancel();socket=null;audioQueue.clear();start.setEnabled(true);status.setText("收尾等待超时，最后一段可能未完整返回");}},8000);}
    @Override public void onRequestPermissionsResult(int code,String[] permissions,int[] results){super.onRequestPermissionsResult(code,permissions,results);if(code==7){if(results.length>0&&results[0]==PackageManager.PERMISSION_GRANTED)begin();else status.setText("需要麦克风权限才能开始");}}
    @Override protected void onStop(){super.onStop();if(running)end("已暂停：应用离开前台");}
    @Override protected void onDestroy(){net.dispatcher().executorService().shutdown();super.onDestroy();}
}
