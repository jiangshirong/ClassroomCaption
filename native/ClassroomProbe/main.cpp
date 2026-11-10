#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <winhttp.h>
#include <shellapi.h>
#include <shlobj.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <initguid.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>
#include <propvarutil.h>
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>
#include <sstream>
#include <iomanip>
#include <thread>
#include <algorithm>

template<class T> struct Com {
    T* ptr=nullptr;
    ~Com() { if(ptr) ptr->Release(); }
    T** out() { return &ptr; }
    T* operator->() { return ptr; }
    explicit operator bool() const { return ptr!=nullptr; }
};
std::string utf8(const std::wstring& s) {
    if(s.empty()) return {};
    int n=WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0,nullptr,nullptr);
    std::string out(n,0); WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),&out[0],n,nullptr,nullptr); return out;
}
std::string json(const std::wstring& s) {
    std::string result="\"";
    for(unsigned char c: utf8(s)) {
        if(c=='"') result+="\\\""; else if(c=='\\') result+="\\\\";
        else if(c=='\n') result+="\\n"; else if(c=='\r') result+="\\r"; else if(c=='\t') result+="\\t";
        else if(c<32) { char buf[7]; std::snprintf(buf,7,"\\u%04x",c); result+=buf; } else result+=(char)c;
    } return result+"\"";
}
struct Monitor { RECT area{}; int xdpi=96,ydpi=96; };
struct Device { std::wstring name; unsigned channels=0,rate=0,bits=0; HRESULT result=E_FAIL; bool captured=false; double rms=0,peak=0; };
struct Network { std::wstring host; DWORD status=0,error=0; };
struct Report { DWORD major=0,minor=0,build=0; unsigned architecture=0; unsigned long long memory=0; bool writable=false; std::vector<Monitor> monitors; std::vector<Device> devices; std::vector<Network> network; };
std::wstring runtimeReport;
bool captureEnabled=false,networkEnabled=true;
BOOL CALLBACK monitorCallback(HMONITOR handle,HDC,LPRECT,LPARAM context) {
    auto* report=reinterpret_cast<Report*>(context); MONITORINFO info{}; info.cbSize=sizeof(info); GetMonitorInfoW(handle,&info);
    Monitor monitor; monitor.area=info.rcWork;
    HMODULE library=LoadLibraryW(L"shcore.dll");
    if(library) { using GetDpi=HRESULT (WINAPI*)(HMONITOR,int,UINT*,UINT*); auto f=reinterpret_cast<GetDpi>(GetProcAddress(library,"GetDpiForMonitor")); UINT x=96,y=96; if(f && SUCCEEDED(f(handle,0,&x,&y))) { monitor.xdpi=x;monitor.ydpi=y; } FreeLibrary(library); }
    report->monitors.push_back(monitor); return TRUE;
}
void audioProbe(Report& report,bool captureAudio) {
    Com<IMMDeviceEnumerator> enumerator;
    if(FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,__uuidof(IMMDeviceEnumerator),reinterpret_cast<void**>(enumerator.out())))) return;
    Com<IMMDeviceCollection> collection;
    if(FAILED(enumerator->EnumAudioEndpoints(eCapture,DEVICE_STATE_ACTIVE,collection.out()))) return;
    UINT count=0; collection->GetCount(&count);
    for(UINT i=0;i<count;i++) {
        Device info; Com<IMMDevice> device; if(FAILED(collection->Item(i,device.out()))) continue;
        Com<IPropertyStore> properties; if(SUCCEEDED(device->OpenPropertyStore(STGM_READ,properties.out()))) { PROPVARIANT value; PropVariantInit(&value); if(SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName,&value)) && value.vt==VT_LPWSTR) info.name=value.pwszVal; PropVariantClear(&value); }
        Com<IAudioClient> client;
        info.result=device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,reinterpret_cast<void**>(client.out()));
        if(SUCCEEDED(info.result)) {
            WAVEFORMATEX* format=nullptr; info.result=client->GetMixFormat(&format);
            if(SUCCEEDED(info.result) && format) {
                info.channels=format->nChannels; info.rate=format->nSamplesPerSec; info.bits=format->wBitsPerSample;
                if(captureAudio) {
                    info.result=client->Initialize(AUDCLNT_SHAREMODE_SHARED,0,1000000,0,format,nullptr);
                    if(SUCCEEDED(info.result)) {
                        Com<IAudioCaptureClient> reader; info.result=client->GetService(__uuidof(IAudioCaptureClient),reinterpret_cast<void**>(reader.out()));
                        if(SUCCEEDED(info.result)) {
                            info.result=client->Start();
                            if(SUCCEEDED(info.result)) {
                                ULONGLONG deadline=GetTickCount64()+600; double sum=0; unsigned long long samples=0;
                                bool floating=format->wFormatTag==WAVE_FORMAT_IEEE_FLOAT;
                                if(format->wFormatTag==WAVE_FORMAT_EXTENSIBLE && format->cbSize>=22) floating=IsEqualGUID(reinterpret_cast<WAVEFORMATEXTENSIBLE*>(format)->SubFormat,KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
                                while(GetTickCount64()<deadline) {
                                    Sleep(20); UINT32 frames=0;
                                    while(SUCCEEDED(reader->GetNextPacketSize(&frames)) && frames) {
                                        BYTE* data=nullptr; DWORD flags=0; if(FAILED(reader->GetBuffer(&data,&frames,&flags,nullptr,nullptr))) break;
                                        unsigned length=frames*format->nChannels; info.captured=true;
                                        for(unsigned n=0;n<length;n++) {
                                            double v=0;
                                            if(!(flags&AUDCLNT_BUFFERFLAGS_SILENT)) {
                                                if(floating && format->wBitsPerSample==32) v=reinterpret_cast<float*>(data)[n];
                                                else if(format->wBitsPerSample==16) v=reinterpret_cast<short*>(data)[n]/32768.0;
                                                else if(format->wBitsPerSample==32) v=reinterpret_cast<int*>(data)[n]/2147483648.0;
                                                else if(format->wBitsPerSample==24) { const BYTE* p=data+n*3; int v24=p[0]|(p[1]<<8)|(p[2]<<16); if(v24&0x800000) v24|=~0xffffff; v=v24/8388608.0; }
                                            }
                                            if(std::isfinite(v)) { sum+=v*v;info.peak=std::max(info.peak,std::abs(v));samples++; }
                                        }
                                        reader->ReleaseBuffer(frames);
                                    }
                                }
                                client->Stop(); if(samples) info.rms=std::sqrt(sum/samples);
                            }
                        }
                    }
                }
                CoTaskMemFree(format);
            }
        }
        report.devices.push_back(info);
    }
}
Network networkProbe(const wchar_t* host,const wchar_t* path) {
    Network result; result.host=host;
    HINTERNET session=WinHttpOpen(L"ClassroomProbe/1.0",WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,WINHTTP_NO_PROXY_NAME,WINHTTP_NO_PROXY_BYPASS,0);
    if(!session) { result.error=GetLastError();return result; }
    WinHttpSetTimeouts(session,4000,4000,4000,4000);
    DWORD tls=0x00000800; WinHttpSetOption(session,WINHTTP_OPTION_SECURE_PROTOCOLS,&tls,sizeof(tls));
    HINTERNET connection=WinHttpConnect(session,host,INTERNET_DEFAULT_HTTPS_PORT,0);
    HINTERNET request=connection?WinHttpOpenRequest(connection,L"GET",path,nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,WINHTTP_FLAG_SECURE):nullptr;
    if(request) {
        DWORD redirects=WINHTTP_DISABLE_REDIRECTS; WinHttpSetOption(request,WINHTTP_OPTION_DISABLE_FEATURE,&redirects,sizeof(redirects));
        if(WinHttpSendRequest(request,WINHTTP_NO_ADDITIONAL_HEADERS,0,WINHTTP_NO_REQUEST_DATA,0,0,0) && WinHttpReceiveResponse(request,nullptr)) {
            DWORD length=sizeof(result.status); WinHttpQueryHeaders(request,WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,WINHTTP_HEADER_NAME_BY_INDEX,&result.status,&length,WINHTTP_NO_HEADER_INDEX);
        } else result.error=GetLastError();
    } else result.error=GetLastError();
    if(request) WinHttpCloseHandle(request); if(connection) WinHttpCloseHandle(connection); WinHttpCloseHandle(session); return result;
}
Report collect(const std::wstring& output,bool captureAudio) {
    Report report;
    using RtlVersion=LONG (WINAPI*)(OSVERSIONINFOW*); auto rtl=reinterpret_cast<RtlVersion>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlGetVersion"));
    OSVERSIONINFOW version{};version.dwOSVersionInfoSize=sizeof(version); if(rtl && rtl(&version)==0) { report.major=version.dwMajorVersion;report.minor=version.dwMinorVersion;report.build=version.dwBuildNumber; }
    SYSTEM_INFO system{};GetNativeSystemInfo(&system);report.architecture=system.wProcessorArchitecture;
    MEMORYSTATUSEX memory{};memory.dwLength=sizeof(memory);GlobalMemoryStatusEx(&memory);report.memory=memory.ullTotalPhys;
    EnumDisplayMonitors(nullptr,nullptr,monitorCallback,reinterpret_cast<LPARAM>(&report));
    std::wstring check=output+L"\\.write-check-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64());HANDLE file=CreateFileW(check.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY|FILE_FLAG_DELETE_ON_CLOSE,nullptr);
    report.writable=file!=INVALID_HANDLE_VALUE;if(report.writable) CloseHandle(file);
    CoInitializeEx(nullptr,COINIT_MULTITHREADED);audioProbe(report,captureAudio);CoUninitialize();
    if(networkEnabled)report.network.push_back(networkProbe(L"dashscope.aliyuncs.com",L"/api-ws/v1/inference"));
    return report;
}
std::wstring readable(const Report& report) {
    std::wostringstream out;out<<L"俄语课堂环境检测 v2.0\r\n独立 x86 Win32 进程，不包含主程序实际 WPF 渲染等级。\r\n";
    out<<L"主程序运行诊断："<<(runtimeReport.empty()?L"未提供（独立运行时无法采集主程序状态）":runtimeReport)<<L"\r\n\r\n";
    out<<L"Windows "<<report.major<<L"."<<report.minor<<L"，内部版本 "<<report.build<<L"\r\n";
    out<<L"系统架构："<<(report.architecture==9?L"x64":report.architecture==12?L"ARM64":report.architecture==0?L"x86":L"其他")<<L"（本检测程序为 x86）\r\n";
    out<<L"物理内存："<<std::fixed<<std::setprecision(1)<<report.memory/1073741824.0<<L" GB\r\n报告目录可写："<<(report.writable?L"是":L"否")<<L"\r\n\r\n";
    for(size_t i=0;i<report.monitors.size();i++) { auto m=report.monitors[i];out<<L"显示器 "<<i+1<<L"：工作区 "<<m.area.right-m.area.left<<L" × "<<m.area.bottom-m.area.top<<L"，缩放约 "<<m.xdpi*100/96<<L"%\r\n"; }
    out<<L"\r\n音频输入（采集仅在内存，不保存声音）：\r\n";
    if(report.devices.empty()) out<<L"未发现启用的音频输入设备。\r\n";
    for(auto d: report.devices) {
        out<<L"• "<<d.name<<L"\r\n  "<<d.rate<<L" Hz / "<<d.bits<<L" bit / "<<d.channels<<L" 声道；采集"<<(d.captured?L"成功":L"未成功或未测试")<<L"\r\n";
        out<<L"  RMS "<<std::setprecision(5)<<d.rms<<L"，峰值 "<<d.peak<<L"，HRESULT 0x"<<std::hex<<(unsigned long)d.result<<std::dec<<L"\r\n";
    }
    out<<L"\r\n加密网络连接（未携带密钥）：\r\n";
    for(auto n: report.network) out<<L"• "<<n.host<<L"：HTTP "<<n.status<<L"，系统错误 "<<n.error<<L"\r\n";
    out<<L"\r\nHTTP 401/403 等响应也能说明该入口已可达，不代表密钥或模型可用。\r\n基础网络检测使用公共域名；LiveTranslate 的 Workspace 入口请在主程序测试。\r\n\r\n检测工具能运行不代表主程序兼容。旧 Windows 的 TLS、驱动和程序限制需现场确认。\r\n";
    return out.str();
}
std::string structured(const Report& report) {
    std::ostringstream out;out<<"{\"version\":1,\"probeArchitecture\":\"x86\",\"os\":{\"major\":"<<report.major<<",\"minor\":"<<report.minor<<",\"build\":"<<report.build<<",\"architectureCode\":"<<report.architecture<<"},\"memoryBytes\":"<<report.memory<<",\"directoryWritable\":"<<(report.writable?"true":"false")<<",\"monitors\":[";
    for(size_t i=0;i<report.monitors.size();i++) { if(i)out<<',';auto m=report.monitors[i];out<<"{\"left\":"<<m.area.left<<",\"top\":"<<m.area.top<<",\"width\":"<<m.area.right-m.area.left<<",\"height\":"<<m.area.bottom-m.area.top<<",\"dpi\":"<<m.xdpi<<'}'; }
    out<<"],\"audioInputs\":[";
    for(size_t i=0;i<report.devices.size();i++) { if(i)out<<',';auto d=report.devices[i];out<<"{\"name\":"<<json(d.name)<<",\"rate\":"<<d.rate<<",\"channels\":"<<d.channels<<",\"bits\":"<<d.bits<<",\"hresult\":"<<(unsigned long)d.result<<",\"captured\":"<<(d.captured?"true":"false")<<",\"rms\":"<<d.rms<<",\"peak\":"<<d.peak<<'}'; }
    out<<"],\"network\":[";
    for(size_t i=0;i<report.network.size();i++) { if(i)out<<',';auto n=report.network[i];out<<"{\"host\":"<<json(n.host)<<",\"httpStatus\":"<<n.status<<",\"systemError\":"<<n.error<<'}'; }
    out<<"],\"collectionContext\":{\"collector\":\"nativeWin32\",\"wpfRenderTier\":null,\"wpfUnavailableReason\":\"Independent x86 Win32 probe does not host the WPF application\",\"runtimeReport\":"<<(runtimeReport.empty()?"null":json(runtimeReport))<<",\"audioCaptureRequested\":"<<(captureEnabled?"true":"false")<<",\"networkProbeRequested\":"<<(networkEnabled?"true":"false")<<"},\"containsCredentials\":false,\"containsRecordedAudio\":false}";
    return out.str();
}
bool writeFile(const std::wstring& path,const std::string& content) { FILE* file=_wfopen(path.c_str(),L"wb");if(!file)return false;bool ok=std::fwrite(content.data(),1,content.size(),file)==content.size();std::fclose(file);return ok; }
std::wstring outputDirectory;HWND editBox,runButton,captureCheck;std::wstring completedText;bool running=false;
bool runProbe(bool captureAudio) {
    CreateDirectoryW(outputDirectory.c_str(),nullptr);auto report=collect(outputDirectory,captureAudio);
    SYSTEMTIME t;GetLocalTime(&t);wchar_t stem[100];swprintf(stem,100,L"\\环境检测-%04d%02d%02d-%02d%02d%02d-%lu",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,GetCurrentProcessId());
    auto path=outputDirectory+stem; auto text=readable(report);
    bool ok=writeFile(path+L".json",structured(report)) && writeFile(path+L".txt",std::string("\xEF\xBB\xBF")+utf8(text));
    completedText=ok?text+L"\r\n报告已保存至：\r\n"+path+L".json\r\n"+path+L".txt":text+L"\r\n报告保存失败。请将检测工具放到可写目录后重试。";
    return ok;
}
LRESULT CALLBACK windowProcedure(HWND window,UINT message,WPARAM w,LPARAM l) {
    switch(message) {
    case WM_CREATE: {
        auto font=(HFONT)GetStockObject(DEFAULT_GUI_FONT);
        runButton=CreateWindowW(L"BUTTON",L"开始检测",WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON,18,16,350,38,window,(HMENU)1,nullptr,nullptr);
        captureCheck=CreateWindowW(L"BUTTON",L"另行测试麦克风（每设备约 0.6 秒，仅内存）",WS_CHILD|WS_VISIBLE|BS_AUTOCHECKBOX,18,60,650,28,window,(HMENU)2,nullptr,nullptr);
        SendMessage(captureCheck,BM_SETCHECK,captureEnabled?BST_CHECKED:BST_UNCHECKED,0);
        editBox=CreateWindowExW(WS_EX_CLIENTEDGE,L"EDIT",L"检测系统、显示器、输入设备配置和网络。\r\n默认不收音，不采集用户名、序列号或课堂正文。\r\n从主程序打开时，运行诊断另存至 diagnostics。\r\n\r\n结果保存为 JSON 和文本报告。",WS_CHILD|WS_VISIBLE|WS_VSCROLL|ES_MULTILINE|ES_READONLY|ES_AUTOVSCROLL,18,100,730,440,window,nullptr,nullptr,nullptr);
        SendMessage(captureCheck,WM_SETFONT,(WPARAM)font,TRUE);SendMessage(runButton,WM_SETFONT,(WPARAM)font,TRUE);SendMessage(editBox,WM_SETFONT,(WPARAM)font,TRUE);break; }
    case WM_SIZE: MoveWindow(editBox,18,100,LOWORD(l)-36,std::max(60,(int)HIWORD(l)-118),TRUE);break;
    case WM_COMMAND: if(LOWORD(w)==1 && !running) { captureEnabled=SendMessage(captureCheck,BM_GETCHECK,0,0)==BST_CHECKED;running=true;EnableWindow(runButton,FALSE);EnableWindow(captureCheck,FALSE);SetWindowTextW(editBox,L"正在检测，请稍候…"); std::thread([window] { runProbe(captureEnabled);PostMessageW(window,WM_APP+1,0,0); }).detach(); }break;
    case WM_APP+1: running=false;EnableWindow(runButton,TRUE);EnableWindow(captureCheck,TRUE);SetWindowTextW(editBox,completedText.c_str());break;
    case WM_CLOSE: if(!running) DestroyWindow(window);break;
    case WM_DESTROY: PostQuitMessage(0);break;
    default:return DefWindowProcW(window,message,w,l);
    } return 0;
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int show) {
    wchar_t executable[32768];GetModuleFileNameW(nullptr,executable,32768);std::wstring path=executable;outputDirectory=path.substr(0,path.find_last_of(L"\\/"))+L"\\diagnostics";
    int count;LPWSTR* args=CommandLineToArgvW(GetCommandLineW(),&count);bool headless=false;
    for(int i=1;i<count;i++) { if(std::wstring(args[i])==L"--headless") headless=true;else if(std::wstring(args[i])==L"--no-audio")captureEnabled=false;else if(std::wstring(args[i])==L"--audio")captureEnabled=true;else if(std::wstring(args[i])==L"--no-network")networkEnabled=false;else if(std::wstring(args[i])==L"--runtime-report" && i+1<count)runtimeReport=args[++i];else if(std::wstring(args[i])==L"--output-dir" && i+1<count)outputDirectory=args[++i]; }LocalFree(args);
    SetProcessDPIAware();if(headless)return runProbe(captureEnabled)?0:2;
    WNDCLASSW cls{};cls.lpfnWndProc=windowProcedure;cls.hInstance=instance;cls.lpszClassName=L"ClassroomProbeWindow";cls.hCursor=LoadCursor(nullptr,IDC_ARROW);cls.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);RegisterClassW(&cls);
    HWND window=CreateWindowW(cls.lpszClassName,L"俄语课堂 · 独立环境检测（x86）",WS_OVERLAPPEDWINDOW,CW_USEDEFAULT,CW_USEDEFAULT,800,640,nullptr,nullptr,instance,nullptr);ShowWindow(window,show);
    MSG message;while(GetMessageW(&message,nullptr,0,0)>0) { TranslateMessage(&message);DispatchMessageW(&message); }return 0;
}
