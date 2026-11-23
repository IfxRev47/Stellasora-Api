#include <Log.h>
#include <dobby.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cerrno>
#include <link.h>
#include <string>
#include <thread>
#include <unistd.h>
#include <sys/stat.h>

struct FindLibData { const char *name; uintptr_t base; };

int find_lib_cb(struct dl_phdr_info *info, size_t, void *data) {
    auto *fd = reinterpret_cast<FindLibData *>(data);
    if (!info->dlpi_name || info->dlpi_name[0] == '\0') return 0;
    if (strstr(info->dlpi_name, fd->name)) { fd->base = info->dlpi_addr; return 1; }
    return 0;
}

uintptr_t lib_base(const char *name) {
    FindLibData d{name, 0};
    dl_iterate_phdr(find_lib_cb, &d);
    return d.base;
}

struct Il2CppObject {
    void* klass;
    void* monitor;
};

struct Il2CppString {
    Il2CppObject object;
    int32_t length;        // 字符长度（UTF-16 字符数）
    uint16_t chars[0];     // UTF-16 字符串首地址
};
typedef Il2CppString String;


void AppendUtf8(std::string& output, uint32_t codepoint) {
    if (codepoint <= 0x7F) {
        output += static_cast<char>(codepoint);
    } else if (codepoint <= 0x7FF) {
        output += static_cast<char>(0xC0 | (codepoint >> 6));
        output += static_cast<char>(0x80 | (codepoint & 0x3F));
    } else if (codepoint <= 0xFFFF) {
        output += static_cast<char>(0xE0 | (codepoint >> 12));
        output += static_cast<char>(0x80 | ((codepoint >> 6) & 0x3F));
        output += static_cast<char>(0x80 | (codepoint & 0x3F));
    } else if (codepoint <= 0x10FFFF) {
        output += static_cast<char>(0xF0 | (codepoint >> 18));
        output += static_cast<char>(0x80 | ((codepoint >> 12) & 0x3F));
        output += static_cast<char>(0x80 | ((codepoint >> 6) & 0x3F));
        output += static_cast<char>(0x80 | (codepoint & 0x3F));
    }
}

std::string Il2CppStringToStdString(String* il2cppStr) {
    if (!il2cppStr || il2cppStr->length <= 0) {
        return "";
    }

    const char16_t* ptr = reinterpret_cast<const char16_t*>(il2cppStr->chars);
    const int32_t length = il2cppStr->length;

    std::string result;
    result.reserve(length); // 预留空间，对于 ASCII 内容这已经足够，对于非 ASCII 内容会自动扩容

    for (int32_t i = 0; i < length; ++i) {
        uint32_t codepoint = ptr[i];

        // 检查是否为高代理项 (High Surrogate)
        if (codepoint >= 0xD800 && codepoint <= 0xDBFF) {
            // 需要查看下一个字符是否为低代理项 (Low Surrogate)
            if (i + 1 < length) {
                uint32_t low = ptr[i + 1];
                if (low >= 0xDC00 && low <= 0xDFFF) {
                    // 组合成一个完整的码点
                    codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (low - 0xDC00);
                    ++i; // 跳过下一个字符，因为已经处理了
                } else {
                    // 无效的代理对，用替换字符 U+FFFD 代替
                    codepoint = 0xFFFD;
                }
            } else {
                // 字符串末尾孤立的高代理项，无效
                codepoint = 0xFFFD;
            }
        } else if (codepoint >= 0xDC00 && codepoint <= 0xDFFF) {
            // 孤立的低代理项，无效
            codepoint = 0xFFFD;
        }

        AppendUtf8(result, codepoint);
    }

    return result;
}

using GetServerRootURL = Il2CppString* (*)(const void* method);
using GetServerGarbleKey = Il2CppString* (*)(const void* method);

GetServerRootURL originalGetServerRootURL = nullptr;
GetServerGarbleKey originalGetServerGarbleKey = nullptr;

// 必须与服务端 Stella:HandshakeKey 完全一致（32 个 ASCII 字节）。
static constexpr const char16_t* kLocalHandshakeKey = u"StellaLocalHandshakeKey123456789";

static bool replaceIl2CppString(Il2CppString* value, const char16_t* target, const char* label) {
    if (value == nullptr || value->length <= 0) return false;
    size_t targetLength = 0;
    while (target[targetLength] != u'\0') targetLength++;
    if (targetLength > static_cast<size_t>(value->length)) {
        LOGE("%s cannot be replaced: old=%d, new=%zu", label, value->length, targetLength);
        return false;
    }
    std::string before = Il2CppStringToStdString(value);
    memcpy(value->chars, target, targetLength * sizeof(char16_t));
    value->length = static_cast<int32_t>(targetLength);
    value->chars[targetLength] = 0;
    LOGI("%s modified (in-place): %s -> %s", label, before.c_str(),
         Il2CppStringToStdString(value).c_str());
    return true;
}

Il2CppString* replaceServerRootURL(const void* method) {
    Il2CppString* url = originalGetServerRootURL(method);
    if (url != nullptr) {
        std::string current = Il2CppStringToStdString(url);
        if (current != "http://127.0.0.1:1989") {
            replaceIl2CppString(url, u"http://127.0.0.1:1989", "ServerRootURL");
        } else {
            LOGI("Unity ServerRootURL already local: %s", current.c_str());
        }
    }
    return url;
}

Il2CppString* replaceServerGarbleKey(const void* method) {
    Il2CppString* key = originalGetServerGarbleKey(method);
    if (key != nullptr) {
        std::string current = Il2CppStringToStdString(key);
        if (current != "StellaLocalHandshakeKey123456789") {
            replaceIl2CppString(key, kLocalHandshakeKey, "ServerGarbleKey");
        }
    }
    return key;
}

void InstallHook(uintptr_t base) {
    constexpr uintptr_t getServerRootURLRva = 0x044A05D4;
    constexpr uintptr_t getServerGarbleKeyRva = 0x044AD5BC;
    int urlStatus = DobbyHook(
        reinterpret_cast<void*>(base + getServerRootURLRva),
        reinterpret_cast<dobby_dummy_func_t>(replaceServerRootURL),
        reinterpret_cast<dobby_dummy_func_t*>(&originalGetServerRootURL));
    if (urlStatus != 0) {
        LOGE("Failed to attach Unity ServerRootURL hook: %d", urlStatus);
    }
    int keyStatus = DobbyHook(
        reinterpret_cast<void*>(base + getServerGarbleKeyRva),
        reinterpret_cast<dobby_dummy_func_t>(replaceServerGarbleKey),
        reinterpret_cast<dobby_dummy_func_t*>(&originalGetServerGarbleKey));
    if (keyStatus != 0) {
        LOGE("Failed to attach ServerGarbleKey hook: %d", keyStatus);
    }
}




void worker() {
    uintptr_t base = 0;
    for (int i = 0; i < 600000 && base == 0; ++i) {
        base = lib_base("libil2cpp.so");
        if (base == 0) usleep(500);
    }
    if (base == 0) { LOGE("[-] libil2cpp.so not found"); return; }
    LOGI("[+] libil2cpp.so base = 0x%lx", (unsigned long) base);
    InstallHook(base);
}


__attribute__((constructor)) void lib_main() {
    LOGI("Unity server root observer loaded");
    std::thread(worker).detach();
}
