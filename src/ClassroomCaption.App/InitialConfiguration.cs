using System.Text.Json;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public static class InitialConfiguration
{
    // Private builds can carry initial connection settings. Existing local choices win.
    public static void Apply(SettingsStore store, Stream? settings, Stream? credentials)
    {
        if (!File.Exists(store.SettingsPath) && settings is not null)
        {
            var initial = JsonSerializer.Deserialize<AppSettings>(settings);
            if (initial is not null) { initial.Validate(); store.Save(initial); }
        }
        if (!File.Exists(store.CredentialsPath) && credentials is not null)
        {
            var initial = JsonSerializer.Deserialize<ApiCredentials>(credentials);
            if (initial?.Complete == true) store.SaveCredentials(initial);
        }
    }
    public static void ApplyEmbedded(SettingsStore store)
    {
        var assembly = typeof(App).Assembly;
        using var settings = assembly.GetManifestResourceStream("ClassroomCaption.InitialSettings.json");
        using var credentials = assembly.GetManifestResourceStream("ClassroomCaption.InitialCredentials.json");
        Apply(store, settings, credentials);
    }
}
