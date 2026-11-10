using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text;

public static class CachePackageCodec
{
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool CreateCompressor(uint algorithm, IntPtr options, out IntPtr handle);
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool SetCompressorInformation(IntPtr handle, int informationClass, ref uint value, UIntPtr size);
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool Compress(IntPtr handle, byte[] source, UIntPtr sourceSize, byte[] output, UIntPtr outputSize, out UIntPtr size);
    [DllImport("cabinet.dll")] static extern bool CloseCompressor(IntPtr handle);
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool CreateDecompressor(uint algorithm, IntPtr options, out IntPtr handle);
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool Decompress(IntPtr handle, byte[] source, UIntPtr sourceSize, byte[] output, UIntPtr outputSize, out UIntPtr size);
    [DllImport("cabinet.dll")] static extern bool CloseDecompressor(IntPtr handle);

    public sealed class Result
    {
        public long InputBytes, CompressedBytes;
        public double CompressionMs, DecompressionMs;
        public string Sha256;
    }

    public static Result Build(string input, string payload, string metadata, uint blockBytes)
    {
        var bytes=File.ReadAllBytes(input);
        if(bytes.Length==0 || bytes.Length>512*1024*1024)throw new IOException("Invalid payload size.");
        string hash;
        using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        IntPtr handle;
        if(!CreateCompressor(5,IntPtr.Zero,out handle))throw new IOException("Cannot create LZMS compressor.");
        byte[] packed;
        UIntPtr size;
        var watch=Stopwatch.StartNew();
        try
        {
            if(!SetCompressorInformation(handle,1,ref blockBytes,(UIntPtr)4))throw new IOException("Invalid compression block size.");
            Compress(handle,bytes,(UIntPtr)bytes.Length,null,UIntPtr.Zero,out size);
            if(size.ToUInt64()==0||size.ToUInt64()>int.MaxValue)throw new IOException("Invalid compressed size.");
            packed=new byte[(int)size.ToUInt64()];
            if(!Compress(handle,bytes,(UIntPtr)bytes.Length,packed,(UIntPtr)packed.Length,out size))throw new IOException("LZMS compression failed.");
            Array.Resize(ref packed,(int)size.ToUInt64());
        }
        finally{CloseCompressor(handle);}
        var compressionMs=watch.Elapsed.TotalMilliseconds;
        if(!CreateDecompressor(5,IntPtr.Zero,out handle))throw new IOException("Cannot create decompressor.");
        var restored=new byte[bytes.Length];watch.Restart();
        try
        {
            if(!Decompress(handle,packed,(UIntPtr)packed.Length,restored,(UIntPtr)restored.Length,out size)||size.ToUInt64()!=(ulong)bytes.Length)
                throw new IOException("Decompression verification failed.");
        }
        finally{CloseDecompressor(handle);}
        var decompressionMs=watch.Elapsed.TotalMilliseconds;
        for(var i=0;i<bytes.Length;i++)if(bytes[i]!=restored[i])throw new IOException("Payload verification mismatch.");
        File.WriteAllBytes(payload,packed);
        File.WriteAllText(metadata,"CCLZMS2\n"+bytes.Length+"\n"+hash+"\n"+NativeManifest(bytes),new System.Text.UTF8Encoding(false));
        return new Result{InputBytes=bytes.Length,CompressedBytes=packed.Length,Sha256=hash,CompressionMs=compressionMs,DecompressionMs=decompressionMs};
    }
    private static string NativeManifest(byte[] bytes)
    {
        // Supported .NET single-file format, validated here at build time. Refuse
        // unfamiliar formats instead of guessing native extraction paths.
        var signature=new byte[]{0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae};
        var marker=-1;
        for(var i=8;i<=bytes.Length-signature.Length;i++){
            if(bytes[i]!=signature[0])continue;var equal=true;
            for(var j=1;j<signature.Length;j++)if(bytes[i+j]!=signature[j]){equal=false;break;}
            if(equal){marker=i;break;}
        }
        if(marker<8)throw new IOException("Not a supported .NET single-file executable.");
        var header=BitConverter.ToInt64(bytes,marker-8);
        if(header<0||header>bytes.LongLength-12)throw new IOException("Invalid bundle header.");
        using(var reader=new BinaryReader(new MemoryStream(bytes,false),Encoding.UTF8)){
            reader.BaseStream.Position=header;
            if(reader.ReadUInt32()!=6)throw new IOException("Unsupported bundle format; review the cache launcher before updating the SDK.");
            reader.ReadUInt32();var count=reader.ReadInt32();var id=reader.ReadString();
            if(count<1||count>4096||id.Length<1||id.Length>128||id.IndexOfAny(new[]{'\\','/',':','*','?','"','<','>','|'})>=0||id=="."||id=="..")throw new IOException("Invalid bundle identity.");
            reader.ReadInt64();reader.ReadInt64();reader.ReadInt64();reader.ReadInt64();reader.ReadUInt64();
            var native=new List<string>();
            for(var i=0;i<count;i++){
                var offset=reader.ReadInt64();var size=reader.ReadInt64();var compressed=reader.ReadInt64();var type=reader.ReadByte();var name=reader.ReadString();
                if(type!=2)continue;
                if(compressed!=0||offset<0||size<1||offset>bytes.LongLength-size||size>64*1024*1024||name.Length<1||name.Length>128)throw new IOException("Native bundle must be uncompressed and bounded.");
                foreach(var c in name)if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='.'||c=='_'||c=='-'))throw new IOException("Invalid native file name.");
                string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(bytes,(int)offset,(int)size)).Replace("-", "").ToLowerInvariant();
                native.Add(name+" "+size+" "+digest+"\n");
            }
            if(native.Count<1||native.Count>64)throw new IOException("Unexpected native file count.");
            return id+"\n"+native.Count+"\n"+string.Concat(native);
        }
    }
}
