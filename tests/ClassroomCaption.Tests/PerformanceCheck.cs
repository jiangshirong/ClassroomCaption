using System.Diagnostics;
using System.Text.Json;
using ClassroomCaption.Core;

internal static class PerformanceCheck
{
    public static int Run(string output)
    {
        var state=new LiveTranslateState();
        void Send(object e){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(e));state.Accept(doc.RootElement);}
        for(var i=0;i<200;i++)
        {
            Send(new{type="conversation.item.input_audio_transcription.completed",item_id="s"+i,transcript="Судно совершает бортовую качку. "+i});
            Send(new{type="conversation.item.created",previous_item_id="s"+i,item=new{id="t"+i,role="assistant"}});
            Send(new{type="response.text.done",item_id="t"+i,text="船舶发生横摇。"+i});
            Send(new{type="response.done",response=new{status="completed",output=new[]{new{id="t"+i}}}});
        }
        _=state.Snapshot();
        var before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
        for(var i=0;i<2000;i++)
        {
            Send(new{type="conversation.item.input_audio_transcription.delta",item_id="current",delta="слово "});
            if(i%20==0)_=state.Snapshot();
        }
        var updateMs=watch.Elapsed.TotalMilliseconds;var updateBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        before=GC.GetAllocatedBytesForCurrentThread();watch.Restart();
        for(var i=0;i<2000;i++)_=state.Snapshot();
        var idleMs=watch.Elapsed.TotalMilliseconds;var idleBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        var report=new{kind="Synthetic caption state, 200 historical items, 2000 updates/snapshots; no cloud or audio",updateMs,updateBytes,idleMs,idleBytes};
        AtomicFile.Write(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(report));return 0;
    }
}
