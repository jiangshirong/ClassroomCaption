using ClassroomCaption.Core;

internal static class CaptionContinuationTests
{
    public static void Run(Action<string,Action> test,string directory)
    {
        var at=DateTimeOffset.UtcNow;
        CaptionSegment Segment(long id,string russian,string chinese,bool confirmed,int time)=>new(id,russian,chinese,confirmed,confirmed,at.AddSeconds(time),[]);
        test("Paused bilingual content and unfinished Russian survive a new transport with fresh IDs",()=>
        {
            var previous=new CaptionState([Segment(1,"Первая фраза.","第一句。",true,0)],
                [Segment(2,"Вторая фраза.","第二句暂译。",false,2)],"Продолжение",1,87,[Segment(3,"Фрагмент.","片段。",false,1)]);
            var continuation=new CaptionContinuation(previous);var restored=continuation.Combine(new([],[],"",0,0));
            if(CaptionFlowComposer.Compose(previous)!=CaptionFlowComposer.Compose(restored)||restored.Revision<=previous.Revision)throw new Exception("Paused text was cleared or its revision went backwards");
            var fresh=new CaptionState([Segment(1,"Третья фраза.","第三句。",true,3)],[Segment(2,"Четвёртая.","第四句。",false,4)],"",0,1);
            var combined=continuation.Combine(fresh);var display=CaptionFlowComposer.Compose(combined);
            if(!ReferenceEquals(combined,continuation.Combine(fresh)))throw new Exception("Unchanged resumed state is rebuilt on every UI tick");
            if(!display.Russian.StartsWith(CaptionFlowComposer.Compose(previous).Russian)||!display.Chinese.StartsWith(CaptionFlowComposer.Compose(previous).Chinese))throw new Exception("New text did not continue the old reading prefix");
            var ids=combined.Confirmed.Concat(combined.Interrupted??[]).Concat(combined.Drafts).Select(s=>s.Id).ToArray();
            if(ids.Distinct().Count()!=ids.Length||continuation.Map(fresh.Confirmed[0]).Id<=4)throw new Exception("Provider IDs collided with paused text");
        });
        test("Repeated resumes preserve the same local transcript without overwriting earlier translations",()=>
        {
            using var session=new SessionStore(Path.Combine(directory,"continuation"));
            var state=new CaptionState([],[],"",0,0);
            for(var cycle=0;cycle<8;cycle++)
            {
                var continuation=new CaptionContinuation(state);var next=Segment(1,$"Фраза {cycle}.",$"译文{cycle}。",true,cycle);
                session.SaveCommitted(continuation.Map(next));
                state=continuation.Combine(new([next],[],"",0,1));session.SaveDraft(state);
            }
            var text=SessionStore.ReadText(session.FilePath);
            for(var cycle=0;cycle<8;cycle++)if(text.Split($"译文{cycle}。").Length!=2)throw new Exception("Resuming duplicated or lost a saved translation");
        });
        test("Paused prefixes remain bounded across hundreds of resumes",()=>
        {
            var state=new CaptionState([],[],"",0,0);long lastId=0,lastRevision=0;
            for(var cycle=0;cycle<360;cycle++)
            {
                state=new CaptionContinuation(state).Combine(new([Segment(1,$"Фраза {cycle}.",$"第{cycle}句。",true,cycle)],[],"",0,1));
                var latest=state.Confirmed[^1];
                if(latest.Id<=lastId||state.Revision<=lastRevision||state.Confirmed.Count>200)throw new Exception("Resume IDs, revisions or memory bounds regressed");
                lastId=latest.Id;lastRevision=state.Revision;
            }
            if(!CaptionFlowComposer.Compose(state).Chinese.EndsWith("第359句。"))throw new Exception("Newest continued text is missing");
        });
        test("Paused drafts and untranslated source export once while the next transport is disconnected",()=>
        {
            using var session=new SessionStore(Path.Combine(directory,"paused-drafts"));
            var previous=new CaptionState([], [Segment(4,"Последняя фраза.","最后的暂译。",false,0)],"Ещё слова",2,80);
            session.SaveDraft(previous);
            var prefix=new CaptionContinuation(previous).Combine(new([],[],"",0,0));
            foreach(var pending in prefix.Interrupted!)session.SaveArchived(pending);
            session.SaveDraft(prefix);
            foreach(var memory in new CaptionState?[] {null,prefix})
            {
                var text=SessionStore.ReadText(session.FilePath,memory);
                if(text.Split("最后的暂译。").Length!=2||text.Split("Ещё слова").Length!=2)throw new Exception("Paused source or translation is duplicated in the local record");
            }
        });
    }
}
