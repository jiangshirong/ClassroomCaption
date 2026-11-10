using System.Text.Json;
using ClassroomCaption.Core;

internal static class LongClassroomTests
{
    public static void Run(Action<string,Action> test,string root)
    {
        static void Event(LiveTranslateState state,object value)
        { using var json=JsonDocument.Parse(JsonSerializer.Serialize(value));state.Accept(json.RootElement); }
        static void Source(LiveTranslateState state,int id)=>Event(state,new {type="conversation.item.input_audio_transcription.completed",item_id="s"+id,transcript="Фраза "+id});
        static void Finish(LiveTranslateState state,int id)
        {
            Event(state,new {type="conversation.item.created",previous_item_id="s"+id,item=new {id="t"+id,role="assistant"}});
            Event(state,new {type="response.text.done",item_id="t"+id,text="句子"+id});
            Event(state,new {type="response.done",response=new {status="completed",output=new[]{new {id="t"+id}}}});
        }
        test("A long classroom survives early unfinished sources beyond item 201 and archives them",()=>
        {
            var state=new LiveTranslateState();var archived=new List<CaptionSegment>();var saved=0;
            state.Archived+=archived.Add;state.Committed+=_=>saved++;
            Source(state,1); // early silence/misrecognition gets no translation completion
            for(var id=2;id<=3000;id++){Source(state,id);Finish(state,id);}
            var snapshot=state.Snapshot();
            if(saved!=2999||archived.Count!=1||archived[0].Id!=1||archived[0].Confirmed||snapshot.Confirmed.Count!=200||snapshot.Confirmed[^1].Chinese!="句子3000")throw new Exception("Long-classroom state lost the latest caption or stopped at its history limit");
        });
        test("Long-session eviction retains recent items in chronological order and ignores delayed old events",()=>
        {
            var state=new LiveTranslateState();
            for(var id=1;id<=500;id++){Source(state,id);Finish(state,id);}
            Source(state,1);Finish(state,1);
            var snapshot=state.Snapshot();
            if(snapshot.Confirmed.Count!=200||snapshot.Confirmed[0].Chinese!="句子301"||snapshot.Confirmed[^1].Chinese!="句子500"||snapshot.Drafts.Count!=0)throw new Exception("Dictionary slot reuse evicted new captions or old events resurrected a retired item");
        });
        test("Unmatched translation events remain bounded without stopping subsequent valid captions",()=>
        {
            var state=new LiveTranslateState();
            for(var id=0;id<800;id++)Event(state,new {type="response.text.delta",item_id="orphan"+id,delta="暂译"});
            Source(state,1000);Finish(state,1000);
            if(state.Snapshot().Confirmed.Single().Chinese!="句子1000")throw new Exception("Orphan events stopped a valid caption");
        });
        test("Long classroom persists all confirmed and unfinished history and a safe stop reason",()=>
        {
            using var store=new SessionStore(Path.Combine(root,"long-classroom"));var state=new LiveTranslateState();
            state.Committed+=store.SaveCommitted;state.Archived+=store.SaveArchived;
            Source(state,1);
            for(var id=2;id<=800;id++){Source(state,id);Finish(state,id);}
            store.Status("translation_error","语音连接中断");store.Metrics(new {stopReason="translation_error"});
            var history=SessionStore.ReadEntries(store.FilePath);
            if(history.Count!=800||!history.Any(x=>x.Russian=="Фраза 1")||!history.Any(x=>x.Chinese=="句子800"))throw new Exception("Display eviction deleted classroom history");
            using var reader=new StreamReader(new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
            if(!reader.ReadToEnd().Contains("translation_error"))throw new Exception("Stop reason was not persisted");
        });
    }
}
