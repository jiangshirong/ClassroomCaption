#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsvc.h>
#include <wincrypt.h>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

struct ServiceHandle {
    SC_HANDLE value;
    ~ServiceHandle() { if (value) CloseServiceHandle(value); }
};

fs::path driverFile(std::wstring path, const wchar_t* name) {
    if (path.empty())
        return fs::path(LR"(System32\drivers)") / (std::wstring(name) + L".sys");
    if (path.front() == L'"') {
        const auto end = path.find(L'"', 1);
        if (end != std::wstring::npos) path = path.substr(1, end - 1);
    }
    if (path.starts_with(LR"(\??\)")) path.erase(0, 4);
    if (path.size() >= 12 && _wcsnicmp(path.c_str(), LR"(\SystemRoot\)", 12) == 0)
        path = path.substr(12);
    const DWORD size = ExpandEnvironmentStringsW(path.c_str(), nullptr, 0);
    if (!size) return path;
    std::wstring expanded(size, L'\0');
    if (!ExpandEnvironmentStringsW(path.c_str(), expanded.data(), size)) return path;
    expanded.resize(size - 1);
    return expanded;
}

void writeVersion(std::wostream& out, const fs::path& file) {
    DWORD unused = 0;
    const DWORD size = GetFileVersionInfoSizeW(file.c_str(), &unused);
    if (!size) {
        out << L"文件版本信息不可读，Windows 错误码 " << GetLastError() << L"\r\n";
        return;
    }
    std::vector<BYTE> bytes(size);
    if (!GetFileVersionInfoW(file.c_str(), 0, size, bytes.data())) {
        out << L"读取版本信息失败，Windows 错误码 " << GetLastError() << L"\r\n";
        return;
    }
    struct Translation { WORD language, codePage; };
    Translation* translations = nullptr;
    UINT length = 0;
    if (!VerQueryValueW(bytes.data(), LR"(\VarFileInfo\Translation)", reinterpret_cast<void**>(&translations), &length)) length = 0;
    for (const auto& field : {L"CompanyName", L"ProductName", L"FileDescription", L"FileVersion", L"OriginalFilename"}) {
        const wchar_t* value = L"未提供";
        for (UINT i = 0; i < length / sizeof(Translation); ++i) {
            wchar_t key[96];
            swprintf(key, 96, LR"(\StringFileInfo\%04x%04x\%ls)", translations[i].language, translations[i].codePage, field);
            wchar_t* found = nullptr;
            UINT count = 0;
            if (VerQueryValueW(bytes.data(), key, reinterpret_cast<void**>(&found), &count) && count > 1) {
                value = found;
                break;
            }
        }
        out << field << L" = " << value << L"\r\n";
    }
}

void writeSigner(std::wostream& out, const fs::path& file) {
    HCERTSTORE store = nullptr;
    HCRYPTMSG message = nullptr;
    if (CryptQueryObject(CERT_QUERY_OBJECT_FILE, file.c_str(), CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
                         CERT_QUERY_FORMAT_FLAG_BINARY, 0, nullptr, nullptr, nullptr, &store, &message, nullptr)) {
        DWORD size = 0;
        if (CryptMsgGetParam(message, CMSG_SIGNER_INFO_PARAM, 0, nullptr, &size) && size) {
            std::vector<BYTE> bytes(size);
            if (CryptMsgGetParam(message, CMSG_SIGNER_INFO_PARAM, 0, bytes.data(), &size)) {
                const auto* signer = reinterpret_cast<const CMSG_SIGNER_INFO*>(bytes.data());
                CERT_INFO info{};
                info.Issuer = signer->Issuer;
                info.SerialNumber = signer->SerialNumber;
                const auto cert = CertFindCertificateInStore(store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                    0, CERT_FIND_SUBJECT_CERT, &info, nullptr);
                if (cert) {
                    wchar_t name[512]{};
                    CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, name, 512);
                    CertFreeCertificateContext(cert);
                    if (*name) out << L"嵌入签名者（未校验信任链） = " << name << L"\r\n";
                }
            }
        }
        if (message) CryptMsgClose(message);
        if (store) CertCloseStore(store, 0);
    } else {
        out << L"未读到嵌入签名。驱动也可能通过系统目录签名。\r\n";
    }
}

void query(std::wostream& out, SC_HANDLE manager, const fs::path& windows, const wchar_t* name) {
    out << L"\r\n[" << name << L"]\r\n";
    ServiceHandle service{OpenServiceW(manager, name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS)};
    if (!service.value) {
        out << L"无法查询，Windows 错误码 " << GetLastError() << L"（1060 = 未注册，5 = 拒绝访问）\r\n";
        return;
    }
    SERVICE_STATUS state{};
    if (QueryServiceStatus(service.value, &state))
        out << L"运行状态 = " << state.dwCurrentState << (state.dwCurrentState == SERVICE_RUNNING ? L" (RUNNING)" : L"") << L"\r\n";
    DWORD size = 0;
    QueryServiceConfigW(service.value, nullptr, 0, &size);
    if (!size) {
        out << L"无法读取配置，Windows 错误码 " << GetLastError() << L"\r\n";
        return;
    }
    std::vector<BYTE> bytes(size);
    const auto config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(bytes.data());
    if (!QueryServiceConfigW(service.value, config, size, &size)) {
        out << L"无法读取配置，Windows 错误码 " << GetLastError() << L"\r\n";
        return;
    }
    const std::wstring path = config->lpBinaryPathName ? config->lpBinaryPathName : L"";
    out << L"显示名称 = " << (config->lpDisplayName ? config->lpDisplayName : L"") << L"\r\n"
        << L"启动类型 = " << config->dwStartType << (config->dwStartType == SERVICE_BOOT_START ? L" (BOOT_START)" : L"") << L"\r\n"
        << L"驱动类型 = " << config->dwServiceType << L"\r\n"
        << L"加载组 = " << (config->lpLoadOrderGroup ? config->lpLoadOrderGroup : L"") << L"\r\n"
        << L"BINARY_PATH_NAME = " << (path.empty() ? L"未提供" : path) << L"\r\n";
    auto file = driverFile(path, name);
    if (!file.is_absolute()) file = windows / file;
    out << (path.empty() ? L"尝试默认文件位置 = " : L"驱动文件 = ") << file.wstring() << L"\r\n";
    if (GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES) {
        out << L"无法访问驱动文件，Windows 错误码 " << GetLastError() << L"\r\n";
        return;
    }
    writeVersion(out, file);
    writeSigner(out, file);
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int) {
    const bool quiet = std::wstring(arguments) == L"--quiet";
    wchar_t executable[32768], windows[32768];
    const DWORD length = GetModuleFileNameW(nullptr, executable, 32768);
    const UINT windowsLength = GetWindowsDirectoryW(windows, 32768);
    if (!length || length >= 32768 || !windowsLength || windowsLength >= 32768) return 1;
    const auto target = fs::path(executable).parent_path() / L"还原驱动检测报告.txt";
    std::wostringstream out;
    out << L"还原驱动检测报告\r\n本工具只查询配置、驱动文件属性和嵌入签名，不修改服务。\r\n";
    ServiceHandle manager{OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT)};
    if (!manager.value) out << L"无法打开服务管理器，Windows 错误码 " << GetLastError() << L"\r\n";
    else for (const auto name : {L"VdiRestore", L"VKDISKCTRL", L"VOiKernel"}) query(out, manager.value, windows, name);
    const auto report = out.str();
    const int count = WideCharToMultiByte(CP_UTF8, 0, report.data(), static_cast<int>(report.size()), nullptr, 0, nullptr, nullptr);
    std::string bytes(count, '\0');
    WideCharToMultiByte(CP_UTF8, 0, report.data(), static_cast<int>(report.size()), bytes.data(), count, nullptr, nullptr);
    std::ofstream file(target, std::ios::binary);
    file << "\xEF\xBB\xBF" << bytes;
    file.close();
    const bool saved = !file.fail();
    if (!quiet) {
        const auto text = saved ? L"报告已保存：\n" + target.wstring() : L"无法写入报告。请将程序放到可写的 E 盘位置后重试。";
        MessageBoxW(nullptr, text.c_str(), L"还原驱动检测", MB_OK | (saved ? MB_ICONINFORMATION : MB_ICONERROR));
    }
    return saved ? 0 : 2;
}
