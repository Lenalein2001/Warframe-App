#include "native.h"
#include <windows.h>
#include <shlobj.h>
#include <propkey.h>
#include <propvarutil.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Data.Xml.Dom.h>
#include <winrt/Windows.UI.Notifications.h>
#include <string>

namespace {
constexpr wchar_t app_id[] = L"WarframeCompanion.Desktop";
void register_shortcut() {
    wchar_t executable[32768]{};
    if (!GetModuleFileNameW(nullptr, executable, 32768)) winrt::throw_last_error();
    PWSTR programs = nullptr;
    winrt::check_hresult(SHGetKnownFolderPath(FOLDERID_Programs, 0, nullptr, &programs));
    std::wstring shortcut = std::wstring(programs) + L"\\Warframe Companion.lnk";
    CoTaskMemFree(programs);
    auto link = winrt::create_instance<IShellLinkW>(CLSID_ShellLink);
    winrt::check_hresult(link->SetPath(executable));
    winrt::check_hresult(link->SetDescription(L"Warframe Companion"));
    auto properties = link.as<IPropertyStore>();
    PROPVARIANT value{};
    winrt::check_hresult(InitPropVariantFromString(app_id, &value));
    const auto result = properties->SetValue(PKEY_AppUserModel_ID, value);
    PropVariantClear(&value);
    winrt::check_hresult(result);
    winrt::check_hresult(properties->Commit());
    winrt::check_hresult(link.as<IPersistFile>()->Save(shortcut.c_str(), TRUE));
}
}

// Called only by the managed notification worker, which owns the COM apartment.
WFC_API int wfc_notify_init() noexcept {
    bool initialized = false;
    try { winrt::init_apartment(); initialized = true; register_shortcut(); return 0; }
    catch (...) { if (initialized) winrt::uninit_apartment(); return static_cast<int>(winrt::to_hresult()); }
}
WFC_API int wfc_notify_show(const wchar_t* title, const wchar_t* body, const wchar_t* tag) noexcept {
    try {
        if (!title || !body || !tag) return E_INVALIDARG;
        using namespace winrt::Windows::Data::Xml::Dom;
        using namespace winrt::Windows::UI::Notifications;
        XmlDocument document;
        document.LoadXml(L"<toast duration='short'><visual><binding template='ToastGeneric'><text/><text/></binding></visual></toast>");
        const auto text = document.GetElementsByTagName(L"text");
        text.Item(0).AppendChild(document.CreateTextNode(title));
        text.Item(1).AppendChild(document.CreateTextNode(body));
        ToastNotification toast(document);
        toast.Tag(tag); toast.Group(L"companion");
        toast.ExpirationTime(winrt::clock::now() + std::chrono::minutes(15));
        ToastNotificationManager::CreateToastNotifier(app_id).Show(toast);
        return 0;
    } catch (...) { return static_cast<int>(winrt::to_hresult()); }
}
WFC_API void wfc_notify_shutdown() noexcept { winrt::uninit_apartment(); }
