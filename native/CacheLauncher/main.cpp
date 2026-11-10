#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define _WIN32_WINNT 0x0602
#include <windows.h>
#include <compressapi.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <filesystem>
#include <string>
#include <vector>
#include <array>
#include <sstream>
#include <stdexcept>
#include <cstring>

namespace fs=std::filesystem;
struct Handle {
    HANDLE value=INVALID_HANDLE_VALUE;
    Handle()=default;
    explicit Handle(HANDLE h):value(h){}
    void reset(HANDLE h=INVALID_HANDLE_VALUE){if(value!=INVALID_HANDLE_VALUE&&value!=nullptr)CloseHandle(value);value=h;}
    ~Handle(){if(value!=INVALID_HANDLE_VALUE&&value!=nullptr)CloseHandle(value);}
    Handle(const Handle&)=delete;Handle& operator=(const Handle&)=delete;
};
struct CacheLock {
    Handle handle;bool owned=false;
    explicit CacheLock(const std::wstring& name):handle(CreateMutexW(nullptr,FALSE,name.c_str())){
        if(!handle.value)throw std::runtime_error("lock");
        DWORD wait=WaitForSingleObject(handle.value,120000);
        owned=wait==WAIT_OBJECT_0||wait==WAIT_ABANDONED;
        if(!owned)throw std::runtime_error("lock timeout");
    }
    ~CacheLock(){if(owned)ReleaseMutex(handle.value);}
};
struct Hasher {
    BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
    Hasher(){if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)throw std::runtime_error("hash");
        if(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)<0){BCryptCloseAlgorithmProvider(algorithm,0);algorithm=nullptr;throw std::runtime_error("hash");}}
    ~Hasher(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
    void add(const unsigned char* data,ULONG size){if(BCryptHashData(hash,const_cast<PUCHAR>(data),size,0)<0)throw std::runtime_error("hash");}
    std::string finish(){std::array<unsigned char,32> bytes{};if(BCryptFinishHash(hash,bytes.data(),32,0)<0)throw std::runtime_error("hash");
        const char* digits="0123456789abcdef";std::string result;for(auto b:bytes){result+=digits[b>>4];result+=digits[b&15];}return result;}
};
struct Resource {const unsigned char* data;DWORD size;};
Resource resource(int id){auto h=GetModuleHandleW(nullptr);auto info=FindResourceW(h,MAKEINTRESOURCEW(id),RT_RCDATA);
    if(!info)throw std::runtime_error("resource");auto loaded=LoadResource(h,info);auto p=static_cast<const unsigned char*>(LockResource(loaded));
    if(!p)throw std::runtime_error("resource");return {p,SizeofResource(h,info)};}
std::wstring fullPath(const std::wstring& path){DWORD count=GetFullPathNameW(path.c_str(),0,nullptr,nullptr);if(!count)throw std::runtime_error("path");
    std::vector<wchar_t> buf(count);if(!GetFullPathNameW(path.c_str(),count,buf.data(),nullptr))throw std::runtime_error("path");return buf.data();}
bool directoryExists(const std::wstring& path){auto flags=GetFileAttributesW(path.c_str());return flags!=INVALID_FILE_ATTRIBUTES&&(flags&FILE_ATTRIBUTE_DIRECTORY);}
void ensureDirectory(const std::wstring& input){fs::path path(fullPath(input));auto current=path.root_path();
    for(const auto& part:path.relative_path()){
        current/=part;auto flags=GetFileAttributesW(current.c_str());
        if(flags==INVALID_FILE_ATTRIBUTES){if(!CreateDirectoryW(current.c_str(),nullptr)&&GetLastError()!=ERROR_ALREADY_EXISTS)throw std::runtime_error("cache directory");flags=GetFileAttributesW(current.c_str());}
        if(flags==INVALID_FILE_ATTRIBUTES||!(flags&FILE_ATTRIBUTE_DIRECTORY)||(flags&FILE_ATTRIBUTE_REPARSE_POINT))throw std::runtime_error("unsafe cache directory");
    }
}
std::wstring chooseDirectory(){IFileOpenDialog* dialog=nullptr;std::wstring result;
    if(SUCCEEDED(CoCreateInstance(CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog)))){
        dialog->SetOptions(FOS_PICKFOLDERS|FOS_FORCEFILESYSTEM|FOS_PATHMUSTEXIST);dialog->SetTitle(L"选择程序缓存目录（请选重启后仍会保留的位置）");
        if(SUCCEEDED(dialog->Show(nullptr))){IShellItem* item=nullptr;if(SUCCEEDED(dialog->GetResult(&item))){PWSTR p=nullptr;if(SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&p))){result=p;CoTaskMemFree(p);}item->Release();}}
        dialog->Release();
    }
    if(result.empty())throw std::runtime_error("cache selection cancelled");return (fs::path(result)/L"课堂字幕程序缓存").wstring();
}
std::wstring defaultRoot(bool dialogs){if(directoryExists(L"E:\\"))return L"E:\\课堂字幕\\程序缓存";
    if(directoryExists(L"D:\\"))return L"D:\\课堂字幕\\程序缓存";if(!dialogs)throw std::runtime_error("no persistent drive");return chooseDirectory();}
std::wstring quote(const std::wstring& value){std::wstring result=L"\"";unsigned slashes=0;
    for(auto c:value){if(c==L'\\'){slashes++;continue;}if(c==L'\"'){result.append(slashes*2+1,L'\\');result+=c;}else{result.append(slashes,L'\\');result+=c;}slashes=0;}
    result.append(slashes*2,L'\\');return result+L"\"";}
std::string utf8(const std::wstring& value){int n=WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    std::string result(n,'\0');WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),result.data(),n,nullptr,nullptr);return result;}
std::string json(const std::wstring& value){std::string result="\"";for(unsigned char c:utf8(value)){if(c=='\\'||c=='\"'){result+='\\';result+=c;}else if(c<32)result+=' ';else result+=c;}return result+'\"';}
void writeBytes(const std::wstring& path,const unsigned char* data,size_t length,bool exclusive=false){Handle file(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,exclusive?CREATE_NEW:CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
    if(file.value==INVALID_HANDLE_VALUE)throw std::runtime_error("write");
    while(length){DWORD written=0;DWORD n=static_cast<DWORD>(length>1048576?1048576:length);if(!WriteFile(file.value,data,n,&written,nullptr)||written!=n)throw std::runtime_error("write");data+=n;length-=n;}
    if(!FlushFileBuffers(file.value))throw std::runtime_error("flush");}
bool validFile(HANDLE file,uint64_t expected,const std::string& sha){if(file==INVALID_HANDLE_VALUE)return false;
    BY_HANDLE_FILE_INFORMATION info{};if(!GetFileInformationByHandle(file,&info)||(info.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT))return false;
    LARGE_INTEGER size{};if(!GetFileSizeEx(file,&size)||static_cast<uint64_t>(size.QuadPart)!=expected)return false;
    Hasher hash;std::vector<unsigned char> buffer(1024*1024);DWORD read=0;
    while(true){if(!ReadFile(file,buffer.data(),static_cast<DWORD>(buffer.size()),&read,nullptr))return false;if(!read)break;hash.add(buffer.data(),read);}return hash.finish()==sha;}
struct State {
    std::wstring root,exe,report;std::string sha;bool reused=false,repaired=false,nativeRepaired=false,childStarted=false;
    double validationMs=0,extractionMs=0;DWORD childPid=0,exitCode=0;
    void save() const {if(report.empty())return;
        std::ostringstream out;out<<"{\"cacheRoot\":"<<json(root)<<",\"cacheExe\":"<<json(exe)<<",\"sha256\":\""<<sha<<"\",\"reused\":"<<(reused?"true":"false")
            <<",\"repaired\":"<<(repaired?"true":"false")<<",\"nativeRepaired\":"<<(nativeRepaired?"true":"false")<<",\"validationMs\":"<<validationMs<<",\"extractionMs\":"<<extractionMs
            <<",\"childStarted\":"<<(childStarted?"true":"false")<<",\"childPid\":"<<childPid<<",\"exitCode\":"<<exitCode<<"}";
        auto text=out.str();writeBytes(report,reinterpret_cast<const unsigned char*>(text.data()),text.size());}
};
double elapsed(ULONGLONG start){return static_cast<double>(GetTickCount64()-start);}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){State state;bool noDialog=false,extractOnly=false,explicitRoot=false;std::wstring temporary;
    HRESULT com=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    try{
        int argc=0;auto raw=CommandLineToArgvW(GetCommandLineW(),&argc);if(!raw)throw std::runtime_error("arguments");
        std::vector<std::wstring> args;for(int i=1;i<argc;i++)args.emplace_back(raw[i]);LocalFree(raw);
        std::vector<std::wstring> forwarded;
        for(size_t i=0;i<args.size();i++){
            if(args[i]==L"--program-cache-dir"||args[i]==L"--bootstrap-report"){
                if(i+1>=args.size())throw std::runtime_error("arguments");bool cache=args[i]==L"--program-cache-dir";auto path=fullPath(args[++i]);
                if(cache){state.root=path;explicitRoot=true;}else state.report=path;
            }else if(args[i]==L"--bootstrap-no-dialog")noDialog=true;
            else if(args[i]==L"--bootstrap-extract-only")extractOnly=true;
            else forwarded.push_back(args[i]);
        }
        auto meta=resource(102);if(meta.size>32768)throw std::runtime_error("metadata");
        std::istringstream input(std::string(reinterpret_cast<const char*>(meta.data),meta.size));std::string magic,bundleId;uint64_t length=0;unsigned nativeCount=0;input>>magic>>length>>state.sha>>bundleId>>nativeCount;
        if(magic!="CCLZMS2"||!length||length>512ULL*1024*1024||state.sha.size()!=64||state.sha.find_first_not_of("0123456789abcdef")!=std::string::npos||bundleId.empty()||bundleId.size()>128||bundleId.find_first_of("\\/:*?\"<>|")!=std::string::npos||bundleId=="."||bundleId==".."||nativeCount<1||nativeCount>64)throw std::runtime_error("metadata");
        struct NativeFile {std::string name,sha;uint64_t size;};std::vector<NativeFile> nativeFiles;
        for(unsigned i=0;i<nativeCount;i++){NativeFile file{};input>>file.name>>file.size>>file.sha;
            if(!input||file.name.empty()||file.name.size()>128||file.name.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")!=std::string::npos||file.name=="."||file.name==".."||!file.size||file.size>64ULL*1024*1024||file.sha.size()!=64||file.sha.find_first_not_of("0123456789abcdef")!=std::string::npos)throw std::runtime_error("metadata");nativeFiles.push_back(file);}
        if(state.root.empty())state.root=defaultRoot(!noDialog);
        try{ensureDirectory(state.root);}catch(...){if(explicitRoot||noDialog)throw;
            MessageBoxW(nullptr,L"无法写入程序缓存。请选择重启后仍会保留的可写目录。",L"课堂字幕",MB_OK|MB_ICONINFORMATION);state.root=chooseDirectory();ensureDirectory(state.root);}
        auto folder=fs::path(state.root)/std::wstring(state.sha.begin(),state.sha.end());
        ensureDirectory(folder.wstring());state.exe=(folder/L"ClassroomCaption.exe").wstring();
        auto nativeCache=(folder/L"原生缓存").wstring();
        Handle image;
        {
            CacheLock lock(L"Local\\ClassroomCaption.Cache."+std::wstring(state.sha.begin(),state.sha.end()));
            auto started=GetTickCount64();bool existed=GetFileAttributesW(state.exe.c_str())!=INVALID_FILE_ATTRIBUTES;
            image.reset(CreateFileW(state.exe.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));state.reused=validFile(image.value,length,state.sha);
            state.validationMs=elapsed(started);
            if(!state.reused){image.reset();started=GetTickCount64();state.repaired=existed;auto packed=resource(101);
                if(packed.size>512U*1024*1024)throw std::runtime_error("payload");
                DECOMPRESSOR_HANDLE decoder=nullptr;if(!CreateDecompressor(COMPRESS_ALGORITHM_LZMS,nullptr,&decoder))throw std::runtime_error("decoder");
                void* memory=VirtualAlloc(nullptr,static_cast<SIZE_T>(length),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
                if(!memory){CloseDecompressor(decoder);throw std::runtime_error("memory");}
                SIZE_T written=0;BOOL ok=Decompress(decoder,const_cast<unsigned char*>(packed.data),packed.size,memory,static_cast<SIZE_T>(length),&written);CloseDecompressor(decoder);
                try{if(!ok||written!=length)throw std::runtime_error("decompression");Hasher hash;hash.add(static_cast<const unsigned char*>(memory),static_cast<ULONG>(length));
                    if(hash.finish()!=state.sha)throw std::runtime_error("integrity");
                    temporary=(folder/(L".extract-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64())+L".tmp")).wstring();
                    writeBytes(temporary,static_cast<const unsigned char*>(memory),static_cast<size_t>(length),true);
                }catch(...){VirtualFree(memory,0,MEM_RELEASE);throw;}VirtualFree(memory,0,MEM_RELEASE);
                if(!MoveFileExW(temporary.c_str(),state.exe.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))throw std::runtime_error("cache promotion");temporary.clear();state.extractionMs=elapsed(started);
                image.reset(CreateFileW(state.exe.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
                if(!validFile(image.value,length,state.sha))throw std::runtime_error("cache read");
            }
            auto nativeDirectory=fs::path(nativeCache)/L"ClassroomCaption"/std::wstring(bundleId.begin(),bundleId.end());
            if(GetFileAttributesW(nativeDirectory.c_str())!=INVALID_FILE_ATTRIBUTES){
                ensureDirectory(nativeDirectory.wstring());bool intact=true;
                for(const auto& entry:nativeFiles){auto path=nativeDirectory/std::wstring(entry.name.begin(),entry.name.end());
                    Handle file(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
                    if(!validFile(file.value,entry.size,entry.sha)){intact=false;break;}}
                if(!intact){auto retired=nativeDirectory.wstring()+L".damaged-"+std::to_wstring(GetTickCount64());
                    if(!MoveFileExW(nativeDirectory.c_str(),retired.c_str(),MOVEFILE_WRITE_THROUGH))throw std::runtime_error("native cache repair");state.nativeRepaired=true;}
            }
        }
        if(!extractOnly){
            // Keep the verified image open until CreateProcess maps it; an in-use
            // image is then protected by Windows against in-place replacement.
            std::vector<wchar_t> module(32768);DWORD moduleLength=GetModuleFileNameW(nullptr,module.data(),static_cast<DWORD>(module.size()));
            if(!moduleLength||moduleLength==module.size())throw std::runtime_error("launcher path");
            auto origin=fs::path(std::wstring(module.data(),moduleLength));ensureDirectory(nativeCache);
            if(!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR",nativeCache.c_str())||
               !SetEnvironmentVariableW(L"CLASSROOM_LAUNCH_DIRECTORY",origin.parent_path().c_str())||
               !SetEnvironmentVariableW(L"CLASSROOM_PORTABLE_EXE",origin.c_str()))throw std::runtime_error("environment");
            std::wstring command=quote(state.exe);for(const auto& arg:forwarded)command+=L" "+quote(arg);
            STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
            if(!CreateProcessW(state.exe.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,origin.parent_path().c_str(),&startup,&process))throw std::runtime_error("child start");
            Handle child(process.hProcess);Handle thread(process.hThread);state.childStarted=true;state.childPid=process.dwProcessId;state.save();
            WaitForSingleObject(child.value,INFINITE);GetExitCodeProcess(child.value,&state.exitCode);
        }
        state.save();if(SUCCEEDED(com))CoUninitialize();return static_cast<int>(state.exitCode);
    }catch(...){if(!temporary.empty())DeleteFileW(temporary.c_str());state.exitCode=1;try{state.save();}catch(...){}
        if(!noDialog)MessageBoxW(nullptr,L"程序缓存无法准备或校验失败。请检查磁盘空间和目录权限；也可以重新复制完整 EXE 后重试。课堂记录不会被删除。",L"课堂字幕",MB_OK|MB_ICONERROR);
        if(SUCCEEDED(com))CoUninitialize();return 1;}
}
