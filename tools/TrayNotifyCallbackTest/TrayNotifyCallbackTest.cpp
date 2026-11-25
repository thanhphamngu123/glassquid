#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <tlhelp32.h>
#include <objbase.h>

#include <algorithm>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

namespace {

constexpr GUID kClsidTrayNotify = {0x25DEAD04, 0x1EAC, 0x4911,
    {0x9E, 0x3A, 0xAD, 0x0A, 0x4A, 0xB5, 0x60, 0xFD}};
constexpr IID kIidTrayNotify = {0xFB852B2C, 0x6BAD, 0x4605,
    {0x95, 0x51, 0xF1, 0x5F, 0x87, 0x83, 0x09, 0x35}};
constexpr IID kIidTrayNotifyWin8 = {0xD133CE13, 0x3537, 0x48BA,
    {0x93, 0xA7, 0xAF, 0xCD, 0x5D, 0x20, 0x53, 0xB4}};
constexpr IID kIidNotificationCb = {0xD782CCBA, 0xAFB0, 0x43F1,
    {0x94, 0xDB, 0xFD, 0xA3, 0x77, 0x9E, 0xAC, 0xCB}};
constexpr wchar_t kOverflowClass[] = L"TopLevelWindowForOverflowXamlIsland";
constexpr wchar_t kStopMessage[] = L"TrayNotifyCallbackTest.Emitter.Stop";

struct NotifyItem {
    LPWSTR executablePath;
    LPWSTR tooltip;
    HICON icon;
    HWND window;
    DWORD preference;
    UINT uid;
    GUID guidItem;
};

struct INotificationCB : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Notify(ULONG eventCode, NotifyItem* item) = 0;
};

struct ITrayNotifyWin8 : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE RegisterCallback(INotificationCB* callback, ULONG* cookie) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnregisterCallback(ULONG* cookie) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPreference(const NotifyItem* item) = 0;
    virtual HRESULT STDMETHODCALLTYPE EnableAutoTray(BOOL enabled) = 0;
    virtual HRESULT STDMETHODCALLTYPE DoAction(BOOL enabled) = 0;
};

struct ITrayNotifyLegacy : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE RegisterCallback(INotificationCB* callback) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPreference(const NotifyItem* item) = 0;
    virtual HRESULT STDMETHODCALLTYPE EnableAutoTray(BOOL enabled) = 0;
};

struct TrayEvent {
    ULONG eventCode;
    std::wstring executablePath;
    std::wstring tooltip;
    DWORD preference;
    UINT uid;
    GUID guidItem;
    HWND window;
    HICON icon;
};

std::mutex g_logMutex;
std::mutex g_eventsMutex;
std::vector<TrayEvent> g_events;
std::filesystem::path g_logPath;
DWORD g_mainThreadId = 0;

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (count <= 1) return {};
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), count, nullptr, nullptr);
    result.pop_back();
    return result;
}

void Log(const std::wstring& message) {
    SYSTEMTIME now{};
    GetLocalTime(&now);
    wchar_t stamp[64]{};
    swprintf_s(stamp, L"%04u-%02u-%02uT%02u:%02u:%02u.%03u ", now.wYear, now.wMonth, now.wDay,
        now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    const std::string line = Utf8(std::wstring(stamp) + message) + "\n";
    std::lock_guard lock(g_logMutex);
    std::filesystem::create_directories(g_logPath.parent_path());
    std::ofstream file(g_logPath, std::ios::binary | std::ios::app);
    file.write(line.data(), static_cast<std::streamsize>(line.size()));
    file.flush();
    OutputDebugStringA(line.c_str());
    if (GetConsoleWindow()) {
        DWORD written = 0;
        WriteConsoleA(GetStdHandle(STD_OUTPUT_HANDLE), line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
}

std::wstring EventName(ULONG code) {
    switch (code) {
    case 0: return L"ADD";
    case 1: return L"MODIFY";
    case 2: return L"DELETE";
    default: return L"UNKNOWN_" + std::to_wstring(code);
    }
}

class NotificationCallback final : public INotificationCB {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, kIidNotificationCb)) {
            *object = static_cast<INotificationCB*>(this);
            AddRef();
            Log(L"CALLBACK_QUERY_INTERFACE matched=true");
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG count = --references_;
        if (count == 0) delete this;
        return count;
    }

    HRESULT STDMETHODCALLTYPE Notify(ULONG eventCode, NotifyItem* item) override {
        if (!item) return E_POINTER;
        TrayEvent event{
            eventCode,
            item->executablePath ? item->executablePath : L"",
            item->tooltip ? item->tooltip : L"",
            item->preference,
            item->uid,
            item->guidItem,
            item->window,
            item->icon,
        };
        {
            std::lock_guard lock(g_eventsMutex);
            g_events.push_back(event);
        }
        wchar_t guidText[64]{};
        StringFromGUID2(event.guidItem, guidText, static_cast<int>(std::size(guidText)));
        Log(L"CALLBACK event=" + EventName(eventCode) + L" exe=\"" + event.executablePath
            + L"\" tooltip=\"" + event.tooltip + L"\" preference=" + std::to_wstring(event.preference)
            + L" uid=" + std::to_wstring(event.uid) + L" guid=" + guidText
            + L" hwnd=0x" + std::to_wstring(reinterpret_cast<uintptr_t>(event.window))
            + L" icon=0x" + std::to_wstring(reinterpret_cast<uintptr_t>(event.icon)));
        return S_OK;
    }

private:
    std::atomic<ULONG> references_{1};
};

std::vector<TrayEvent> SnapshotEvents() {
    std::lock_guard lock(g_eventsMutex);
    return g_events;
}

bool IsZalo(const TrayEvent& event) {
    const auto slash = event.executablePath.find_last_of(L"\\/");
    const auto file = slash == std::wstring::npos ? event.executablePath : event.executablePath.substr(slash + 1);
    auto lower = file;
    std::transform(lower.begin(), lower.end(), lower.begin(), towlower);
    auto tooltip = event.tooltip;
    std::transform(tooltip.begin(), tooltip.end(), tooltip.begin(), towlower);
    return lower == L"zalo.exe" || tooltip.find(L"zalo") != std::wstring::npos;
}

std::vector<TrayEvent> MatchingEvents(size_t start, const std::wstring& tooltip,
    std::initializer_list<ULONG> eventCodes) {
    auto events = SnapshotEvents();
    if (start > events.size()) return {};
    events.erase(events.begin(), events.begin() + static_cast<std::ptrdiff_t>(start));
    events.erase(std::remove_if(events.begin(), events.end(), [&](const TrayEvent& event) {
        return event.tooltip != tooltip || std::find(eventCodes.begin(), eventCodes.end(), event.eventCode) == eventCodes.end();
    }), events.end());
    return events;
}

std::wstring FindRunningZaloPath() {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return {};
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    std::wstring result;
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szExeFile, L"Zalo.exe") != 0) continue;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
            if (!process) continue;
            wchar_t path[32768]{};
            DWORD size = static_cast<DWORD>(std::size(path));
            if (QueryFullProcessImageNameW(process, 0, path, &size)) result.assign(path, size);
            CloseHandle(process);
            if (!result.empty()) break;
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return result;
}

std::wstring FindRegistryZaloPath() {
    HKEY settings = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Control Panel\\NotifyIconSettings", 0, KEY_READ, &settings) != ERROR_SUCCESS)
        return {};
    wchar_t keyName[256]{};
    for (DWORD index = 0;; ++index) {
        DWORD keyLength = static_cast<DWORD>(std::size(keyName));
        if (RegEnumKeyExW(settings, index, keyName, &keyLength, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) break;
        HKEY key = nullptr;
        if (RegOpenKeyExW(settings, keyName, 0, KEY_READ, &key) != ERROR_SUCCESS) continue;
        wchar_t path[32768]{};
        DWORD type = 0;
        DWORD size = sizeof(path);
        const auto query = RegQueryValueExW(key, L"ExecutablePath", nullptr, &type, reinterpret_cast<BYTE*>(path), &size);
        RegCloseKey(key);
        if (query != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ)) continue;
        if (type == REG_EXPAND_SZ) {
            wchar_t expanded[32768]{};
            if (ExpandEnvironmentStringsW(path, expanded, static_cast<DWORD>(std::size(expanded))) == 0) continue;
            wcscpy_s(path, std::size(path), expanded);
        }
        if (_wcsicmp(PathFindFileNameW(path), L"Zalo.exe") == 0 && GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) {
            RegCloseKey(settings);
            return path;
        }
    }
    RegCloseKey(settings);
    return {};
}

std::wstring FindZaloPath() {
    auto path = FindRunningZaloPath();
    return path.empty() ? FindRegistryZaloPath() : path;
}

ULONG CountRunningZalo() {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    ULONG count = 0;
    if (Process32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(entry.szExeFile, L"Zalo.exe") == 0) ++count;
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return count;
}

void PumpFor(DWORD milliseconds) {
    const ULONGLONG end = GetTickCount64() + milliseconds;
    MSG message{};
    while (GetTickCount64() < end) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) return;
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(10);
    }
}

std::wstring CurrentExecutablePath() {
    wchar_t path[32768]{};
    const DWORD size = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    return size == 0 || size >= std::size(path) ? L"" : std::wstring(path, size);
}

bool StartEmitter(const std::wstring& tooltip, std::wstring& stopFile, PROCESS_INFORMATION& process) {
    const auto temp = std::filesystem::temp_directory_path();
    stopFile = (temp / (L"TrayNotifyCallbackTest-" + std::to_wstring(GetTickCount64()) + L".stop")).wstring();
    DeleteFileW(stopFile.c_str());
    std::wstring command = L"\"" + CurrentExecutablePath() + L"\" --emit-icon \"" + tooltip + L"\" \"" + stopFile + L"\"";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION created{};
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &created)) {
        Log(L"EMITTER_START_FAIL win32=" + std::to_wstring(GetLastError()));
        return false;
    }
    process = created;
    return true;
}

void StopEmitter(PROCESS_INFORMATION& process, const std::wstring& stopFile, const std::wstring& tooltip) {
    HANDLE file = CreateFileW(stopFile.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (process.hProcess) {
        if (WaitForSingleObject(process.hProcess, 2500) == WAIT_TIMEOUT) {
            TerminateProcess(process.hProcess, 1);
            WaitForSingleObject(process.hProcess, 2000);
        }
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        process = {};
    }
    DeleteFileW(stopFile.c_str());
    PumpFor(700);
    const auto events = MatchingEvents(0, tooltip, {2});
    Log(L"EMITTER_STOP tooltip=\"" + tooltip + L"\" deleteSeen=" + (events.empty() ? L"false" : L"true"));
}

LRESULT CALLBACK EmitterWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_DESTROY) return 0;
    return DefWindowProcW(window, message, wParam, lParam);
}

int EmitIcon(int argc, wchar_t** argv) {
    if (argc < 4) return 64;
    const std::wstring tooltip = argv[2];
    const std::wstring stopFile = argv[3];
    const auto instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = EmitterWindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = L"TrayNotifyCallbackTestEmitterWindow";
    RegisterClassExW(&wc);
    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, L"", WS_POPUP,
        0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!window) return 65;

    NOTIFYICONDATAW icon{};
    icon.cbSize = sizeof(icon);
    icon.hWnd = window;
    icon.uID = 1;
    icon.uFlags = NIF_ICON | NIF_TIP;
    icon.hIcon = LoadIconW(nullptr, IDI_INFORMATION);
    wcsncpy_s(icon.szTip, tooltip.c_str(), _TRUNCATE);
    const BOOL added = Shell_NotifyIconW(NIM_ADD, &icon);
    Log(L"EMITTER_SHELL_NOTIFY_ADD tooltip=\"" + tooltip + L"\" success=" + (added ? L"true" : L"false")
        + L" win32=" + std::to_wstring(GetLastError()));
    if (!added) {
        DestroyWindow(window);
        return 66;
    }
    NOTIFYICONIDENTIFIER identifier{};
    identifier.cbSize = sizeof(identifier);
    identifier.hWnd = window;
    identifier.uID = icon.uID;
    RECT iconRect{};
    const HRESULT rectResult = Shell_NotifyIconGetRect(&identifier, &iconRect);
    Log(L"EMITTER_SHELL_GETRECT tooltip=\"" + tooltip + L"\" HRESULT=0x"
        + std::to_wstring(static_cast<unsigned long>(rectResult)) + L" rect="
        + std::to_wstring(iconRect.left) + L"," + std::to_wstring(iconRect.top) + L"," 
        + std::to_wstring(iconRect.right) + L"," + std::to_wstring(iconRect.bottom));
    icon.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &icon);

    MSG message{};
    while (GetFileAttributesW(stopFile.c_str()) == INVALID_FILE_ATTRIBUTES) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(25);
    }

    const BOOL deleted = Shell_NotifyIconW(NIM_DELETE, &icon);
    Log(L"EMITTER_SHELL_NOTIFY_DELETE tooltip=\"" + tooltip + L"\" success=" + (deleted ? L"true" : L"false")
        + L" win32=" + std::to_wstring(GetLastError()));
    DestroyWindow(window);
    return 0;
}

struct TaskbarGuard {
    HWND window = nullptr;
    RECT original{};
    bool moved = false;

    ~TaskbarGuard() { Restore(); }

    bool MoveOffscreen() {
        if (!window || !GetWindowRect(window, &original)) return false;
        if (!IsWindowVisible(window)) return true;
        moved = SetWindowPos(window, nullptr, -12000, -12000, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE;
        return moved;
    }

    void Restore() {
        if (!moved) return;
        const BOOL restored = SetWindowPos(window, nullptr, original.left, original.top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Log(L"CASE_B_RESTORE success=" + std::wstring(restored ? L"true" : L"false")
            + L" originalRect=" + std::to_wstring(original.left) + L"," + std::to_wstring(original.top)
            + L"," + std::to_wstring(original.right) + L"," + std::to_wstring(original.bottom)
            + L" win32=" + std::to_wstring(GetLastError()));
        moved = false;
        PumpFor(500);
    }
};

std::vector<std::wstring> ReadLogResults();

bool RunEmitterScenario(const std::wstring& tooltip, DWORD lifetimeMs, bool expectDelete) {
    const size_t start = SnapshotEvents().size();
    PROCESS_INFORMATION child{};
    std::wstring stopFile;
    if (!StartEmitter(tooltip, stopFile, child)) return false;
    PumpFor(lifetimeMs);
    const bool addSeen = !MatchingEvents(start, tooltip, {0, 1}).empty();
    StopEmitter(child, stopFile, tooltip);
    const bool deleteSeen = !MatchingEvents(start, tooltip, {2}).empty();
    if (expectDelete)
        Log(L"EMITTER_RESULT tooltip=\"" + tooltip + L"\" addModifySeen=" + (addSeen ? L"true" : L"false")
            + L" deleteSeen=" + (deleteSeen ? L"true" : L"false"));
    return addSeen && (!expectDelete || deleteSeen);
}

void RunTests() {
    PumpFor(1500);

    HWND overflow = FindWindowW(kOverflowClass, nullptr);
    const bool overflowOpen = overflow && IsWindowVisible(overflow);
    const auto zaloPath = FindZaloPath();
    const auto initialEvents = SnapshotEvents();
    const auto initialZalo = std::count_if(initialEvents.begin(), initialEvents.end(), IsZalo);
    const size_t aStart = initialEvents.size();
    Log(L"CASE_A_BEGIN overflowWindow=0x" + std::to_wstring(reinterpret_cast<uintptr_t>(overflow))
        + L" overflowVisible=" + (overflowOpen ? L"true" : L"false")
        + L" zaloPath=\"" + (zaloPath.empty() ? L"<not-found>" : zaloPath)
        + L"\" initialZaloCallbacks=" + std::to_wstring(initialZalo));
    if (overflowOpen) {
        Log(L"CASE_A_RESULT=FAIL reason=native overflow popup was open");
    } else if (zaloPath.empty()) {
        Log(L"CASE_A_RESULT=FAIL reason=Zalo.exe could not be found");
    } else {
        const ULONG existingZalo = CountRunningZalo();
        const auto activation = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", zaloPath.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
        Log(L"CASE_A_OPEN_ZALO shellExecute=" + std::to_wstring(activation) + L" alreadyRunning="
            + (existingZalo ? L"true" : L"false"));
        PumpFor(7000);
        const auto events = SnapshotEvents();
        const auto afterOpen = std::count_if(events.begin() + static_cast<std::ptrdiff_t>(std::min(aStart, events.size())), events.end(), IsZalo);
        const bool pass = activation > 32 && (afterOpen > 0 || initialZalo > 0);
        Log(L"CASE_A_RESULT=" + std::wstring(pass ? L"PASS" : L"FAIL") + L" overflowVisible=false callbackEventsAfterOpen="
            + std::to_wstring(afterOpen) + L" initialSnapshotZalo=" + std::to_wstring(initialZalo));
    }

    TaskbarGuard taskbar;
    taskbar.window = FindWindowW(L"Shell_TrayWnd", nullptr);
    APPBARDATA appbar{};
    appbar.cbSize = sizeof(appbar);
    appbar.hWnd = taskbar.window;
    const UINT appbarState = static_cast<UINT>(SHAppBarMessage(ABM_GETSTATE, &appbar));
    const bool autoHide = (appbarState & ABS_AUTOHIDE) != 0;
    const bool wasVisible = taskbar.window && IsWindowVisible(taskbar.window);
    if (!taskbar.window || !GetWindowRect(taskbar.window, &taskbar.original)) {
        Log(L"CASE_B_RESULT=FAIL reason=Shell_TrayWnd or its bounds unavailable");
    } else {
        Log(L"CASE_B_BEGIN hwnd=0x" + std::to_wstring(reinterpret_cast<uintptr_t>(taskbar.window))
            + L" autoHide=" + (autoHide ? L"true" : L"false") + L" wasVisible=" + (wasVisible ? L"true" : L"false")
            + L" rect=" + std::to_wstring(taskbar.original.left) + L"," + std::to_wstring(taskbar.original.top)
            + L"," + std::to_wstring(taskbar.original.right) + L"," + std::to_wstring(taskbar.original.bottom)
            + L" appbarState=" + std::to_wstring(appbarState));
        const bool visuallyHidden = !wasVisible || taskbar.MoveOffscreen();
        Log(L"CASE_B_HIDE_METHOD=" + std::wstring(wasVisible ? L"move-offscreen" : L"already-not-visible")
            + L" visuallyHidden=" + (visuallyHidden ? L"true" : L"false"));
        const bool addSeen = visuallyHidden && RunEmitterScenario(L"TrayCB-hidden-taskbar", 2500, false);
        taskbar.Restore();
        Log(L"CASE_B_RESULT=" + std::wstring(addSeen ? L"PASS" : L"FAIL")
            + L" taskbarVisuallyHiddenDuringAdd=" + (visuallyHidden ? L"true" : L"false")
            + L" addModifySeen=" + (addSeen ? L"true" : L"false"));
    }

    const bool caseCPass = RunEmitterScenario(L"TrayCB-delete-case", 1000, true);
    Log(L"CASE_C_RESULT=" + std::wstring(caseCPass ? L"PASS" : L"FAIL")
        + L" gracefulAppExitDelete=" + (caseCPass ? L"true" : L"false"));

    const auto lines = ReadLogResults();
    const bool allPass = lines.size() == 3 && std::all_of(lines.begin(), lines.end(), [](const std::wstring& line) {
        return line.find(L"RESULT=PASS") != std::wstring::npos;
    });
    Log(allPass ? L"PROTOTYPE_VERDICT=ALL_THREE_PASS" : L"PROTOTYPE_VERDICT=NOT_ALL_PASS; do not migrate callback source yet");
}

std::vector<std::wstring> ReadLogResults() {
    std::vector<std::wstring> results;
    std::ifstream file(g_logPath, std::ios::binary);
    std::string line;
    while (std::getline(file, line)) {
        auto wideLength = MultiByteToWideChar(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), nullptr, 0);
        if (wideLength <= 0) continue;
        std::wstring wide(static_cast<size_t>(wideLength), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), wide.data(), wideLength);
        if (wide.find(L"CASE_A_RESULT=") != std::wstring::npos
            || wide.find(L"CASE_B_RESULT=") != std::wstring::npos
            || wide.find(L"CASE_C_RESULT=") != std::wstring::npos)
            results.push_back(std::move(wide));
    }
    return results;
}

bool RegisterTrayCallback(IUnknown* unknown, NotificationCallback* callback,
    ITrayNotifyWin8** modern, ITrayNotifyLegacy** legacy, ULONG* cookie) {
    Log(L"QUERY_INTERFACE_BEGIN ITrayNotifyWin8");
    HRESULT hr = unknown->QueryInterface(kIidTrayNotifyWin8, reinterpret_cast<void**>(modern));
    Log(L"QUERY_INTERFACE_RESULT ITrayNotifyWin8 HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
    if (SUCCEEDED(hr)) {
        Log(L"REGISTER_CALL_BEGIN ITrayNotifyWin8");
        hr = (*modern)->RegisterCallback(callback, cookie);
        Log(L"REGISTER interface=ITrayNotifyWin8 IID=D133CE13-3537-48BA-93A7-AFCD5D2053B4 HRESULT=0x"
            + std::to_wstring(static_cast<unsigned long>(hr)) + L" cookie=" + std::to_wstring(*cookie));
        if (SUCCEEDED(hr)) return true;
        (*modern)->Release();
        *modern = nullptr;
        return false;
    }

    hr = unknown->QueryInterface(kIidTrayNotify, reinterpret_cast<void**>(legacy));
    if (FAILED(hr)) {
        Log(L"QUERY_INTERFACE_FAIL ITrayNotifyWin8=0x" + std::to_wstring(static_cast<unsigned long>(hr))
            + L" legacy=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
        return false;
    }
    hr = (*legacy)->RegisterCallback(callback);
    Log(L"REGISTER interface=ITrayNotify IID=FB852B2C-6BAD-4605-9551-F15F87830935 HRESULT=0x"
        + std::to_wstring(static_cast<unsigned long>(hr)));
    return SUCCEEDED(hr);
}

void UnregisterTrayCallback(ITrayNotifyWin8* modern, ITrayNotifyLegacy* legacy, ULONG* cookie) {
    if (modern) {
        const HRESULT hr = modern->UnregisterCallback(cookie);
        Log(L"UNREGISTER interface=ITrayNotifyWin8 HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
        modern->Release();
    } else if (legacy) {
        const HRESULT hr = legacy->RegisterCallback(nullptr);
        Log(L"UNREGISTER interface=ITrayNotify HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
        legacy->Release();
    }
}

BOOL WINAPI ConsoleControlHandler(DWORD control) {
    if (control == CTRL_C_EVENT || control == CTRL_CLOSE_EVENT || control == CTRL_BREAK_EVENT) {
        PostThreadMessageW(g_mainThreadId, WM_QUIT, 0, 0);
        return TRUE;
    }
    return FALSE;
}

int MonitorOrTest(bool runTests) {
    g_mainThreadId = GetCurrentThreadId();
    MSG ensureQueue{};
    PeekMessageW(&ensureQueue, nullptr, 0, 0, PM_NOREMOVE);
    Log(L"COM_INIT_BEGIN");
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Log(L"COM_INIT_RESULT HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(init)));
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) {
        Log(L"COM_INIT_FAIL HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(init)));
        return 2;
    }

    IUnknown* unknown = nullptr;
    Log(L"COCREATE_BEGIN");
    HRESULT hr = CoCreateInstance(kClsidTrayNotify, nullptr, CLSCTX_LOCAL_SERVER, IID_IUnknown,
        reinterpret_cast<void**>(&unknown));
    Log(L"COCREATE_RESULT HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
    if (FAILED(hr)) {
        Log(L"COCREATE_FAIL HRESULT=0x" + std::to_wstring(static_cast<unsigned long>(hr)));
        if (SUCCEEDED(init)) CoUninitialize();
        return 3;
    }

    auto* callback = new NotificationCallback();
    ITrayNotifyWin8* modern = nullptr;
    ITrayNotifyLegacy* legacy = nullptr;
    ULONG cookie = 0;
    if (!RegisterTrayCallback(unknown, callback, &modern, &legacy, &cookie)) {
        callback->Release();
        unknown->Release();
        if (SUCCEEDED(init)) CoUninitialize();
        return 4;
    }
    unknown->Release();
    Log(L"READY Waiting for Explorer add/modify/delete events. No preference API is called.");

    if (runTests) {
        RunTests();
    } else {
        SetConsoleCtrlHandler(ConsoleControlHandler, TRUE);
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    UnregisterTrayCallback(modern, legacy, &cookie);
    callback->Release();
    if (SUCCEEDED(init)) CoUninitialize();
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    const auto cwd = std::filesystem::current_path();
    g_logPath = cwd / L"results" / L"TrayNotifyCallbackTest.log";
    if (argc > 1 && _wcsicmp(argv[1], L"--emit-icon") == 0) return EmitIcon(argc, argv);
    if (argc > 1 && _wcsicmp(argv[1], L"--run-tests") == 0) {
        std::filesystem::create_directories(g_logPath.parent_path());
        std::ofstream(g_logPath, std::ios::binary | std::ios::trunc).close();
        Log(L"PROTOTYPE_START native x64 Explorer tray callback probe; isolated from GlassDesk; callback only; read-only user settings.");
        return MonitorOrTest(true);
    }

    std::filesystem::create_directories(g_logPath.parent_path());
    Log(L"PROTOTYPE_START native x64 Explorer tray callback monitor; press Ctrl+C to unregister and exit.");
    return MonitorOrTest(false);
}
