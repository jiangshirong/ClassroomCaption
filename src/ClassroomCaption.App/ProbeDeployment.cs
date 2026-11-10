using System.Security.Cryptography;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public static class ProbeDeployment
{
    public static string Ensure(string dataRoot)
    {
        using var resource=typeof(App).Assembly.GetManifestResourceStream("ClassroomCaption.ClassroomProbe.exe")
            ??throw new UserFacingException("此版本缺少内置检测工具，请更新软件。");
        using var memory=new MemoryStream();resource.CopyTo(memory);var bytes=memory.ToArray();
        var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var directory=Path.Combine(dataRoot,"tools",hash[..16]);Directory.CreateDirectory(directory);
        var executable=Path.Combine(directory,"ClassroomProbe.exe");
        if(File.Exists(executable)&&SHA256.HashData(File.ReadAllBytes(executable)).SequenceEqual(SHA256.HashData(bytes)))return executable;
        var temporary=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
        try {File.WriteAllBytes(temporary,bytes);File.Move(temporary,executable,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
        return executable;
    }
}
