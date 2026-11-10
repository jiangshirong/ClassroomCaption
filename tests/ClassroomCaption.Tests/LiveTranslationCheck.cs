using System.Text.Json;
using ClassroomCaption.Core;
internal static class LiveTranslationCheck
{
    public static async Task<int> CheckLiveTranslateAsync(string directory)
    {
        try {
            var store = new SettingsStore(directory); var keys = store.LoadCredentials()!;
            await using var client = new LiveTranslateClient(store.Load(), keys.AliyunKey);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await client.StartAsync(timeout.Token);
            for(var i=0;i<10;i++){client.Enqueue(new byte[3200]);await Task.Delay(100,timeout.Token);}
            await client.StopAsync();
            Console.WriteLine(JsonSerializer.Serialize(new{liveTranslateConnected=true, syntheticSilenceSeconds=client.AudioBytes/32000.0})); return 0;
        } catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{liveTranslateConnected=false,error=SafeErrors.Describe(ex)}));return 1;}
    }
}
