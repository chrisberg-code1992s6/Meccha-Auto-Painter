#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>

#include <cerrno>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <fcntl.h>
#include <io.h>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

#include "../include/direct_bridge_abi.hpp"

#pragma comment(lib, "Bcrypt.lib")

namespace
{
    constexpr DWORD RemoteLoadTimeoutMs = 15'000;
    constexpr DWORD RemoteStartTimeoutMs = 15'000;
    constexpr int ModuleLookupAttempts = 20;
    constexpr DWORD ModuleLookupRetryMs = 25;

    auto utf8(const std::wstring& value) -> std::string
    {
        if (value.empty())
        {
            return {};
        }
        const int bytes = WideCharToMultiByte(CP_UTF8,
                                              0,
                                              value.data(),
                                              static_cast<int>(value.size()),
                                              nullptr,
                                              0,
                                              nullptr,
                                              nullptr);
        if (bytes <= 0)
        {
            return {};
        }
        std::string output(static_cast<std::size_t>(bytes), '\0');
        WideCharToMultiByte(CP_UTF8,
                            0,
                            value.data(),
                            static_cast<int>(value.size()),
                            &output[0],
                            bytes,
                            nullptr,
                            nullptr);
        return output;
    }

    auto json_escape(const std::string& value) -> std::string
    {
        std::string escaped;
        escaped.reserve(value.size() + 8);
        for (const char ch : value)
        {
            switch (ch)
            {
            case '\\': escaped += "\\\\"; break;
            case '"': escaped += "\\\""; break;
            case '\n': escaped += "\\n"; break;
            case '\r': escaped += "\\r"; break;
            case '\t': escaped += "\\t"; break;
            default:
                escaped += static_cast<unsigned char>(ch) < 0x20 ? '?' : ch;
                break;
            }
        }
        return escaped;
    }

    auto hex(const std::uint8_t* data, std::size_t size) -> std::string
    {
        if (!data || size == 0)
        {
            return {};
        }
        static constexpr char digits[] = "0123456789abcdef";
        std::string output;
        output.reserve(size * 2);
        for (std::size_t index = 0; index < size; ++index)
        {
            output.push_back(digits[(data[index] >> 4) & 0x0f]);
            output.push_back(digits[data[index] & 0x0f]);
        }
        return output;
    }

    auto write_result(bool success,
                      const char* state,
                      DWORD pid,
                      const BridgeStartBlockV1* block,
                      std::uint32_t port,
                      DWORD win32_error,
                      DWORD winsock_error,
                      const std::string& detail) -> void
    {
        const std::string instance_id = block ? hex(block->instance_guid, sizeof(block->instance_guid)) : "";
        const std::string bridge_hash = block ? hex(block->sha256, sizeof(block->sha256)) : "";
        std::cout << "{\"event\":\"result\",\"protocol\":1"
                  << ",\"success\":" << (success ? "true" : "false")
                  << ",\"state\":\"" << state << "\""
                  << ",\"pid\":" << pid
                  << ",\"instance_id\":\"" << instance_id << "\""
                  << ",\"bridge_hash\":\"" << bridge_hash << "\""
                  << ",\"port\":" << port
                  << ",\"win32\":" << win32_error
                  << ",\"winsock\":" << winsock_error
                  << ",\"detail\":\"" << json_escape(detail) << "\"}\n";
        std::cout.flush();
    }

    auto parse_u32(const wchar_t* text, std::uint32_t& value) -> bool
    {
        if (!text || text[0] == L'\0')
        {
            return false;
        }
        errno = 0;
        wchar_t* end = nullptr;
        const unsigned long long parsed = std::wcstoull(text, &end, 10);
        if (errno != 0 || end == text || *end != L'\0' || parsed == 0 || parsed > (std::numeric_limits<std::uint32_t>::max)())
        {
            return false;
        }
        value = static_cast<std::uint32_t>(parsed);
        return true;
    }

    auto parse_u64(const wchar_t* text, std::uint64_t& value) -> bool
    {
        if (!text || text[0] == L'\0')
        {
            return false;
        }
        errno = 0;
        wchar_t* end = nullptr;
        const unsigned long long parsed = std::wcstoull(text, &end, 10);
        if (errno != 0 || end == text || *end != L'\0' || parsed == 0)
        {
            return false;
        }
        value = static_cast<std::uint64_t>(parsed);
        return true;
    }

    auto normalized_path(const std::wstring& input) -> std::wstring
    {
        if (input.empty())
        {
            return {};
        }
        const DWORD required = GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
        if (required == 0)
        {
            return {};
        }
        std::vector<wchar_t> buffer(static_cast<std::size_t>(required) + 1, L'\0');
        const DWORD written = GetFullPathNameW(input.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
        if (written == 0 || written >= buffer.size())
        {
            return {};
        }
        return std::wstring(buffer.data(), written);
    }

    auto same_path(const std::wstring& left, const std::wstring& right) -> bool
    {
        return !left.empty() && !right.empty() && _wcsicmp(left.c_str(), right.c_str()) == 0;
    }

    auto query_process_image_path(HANDLE process, std::wstring& path, DWORD& win32_error) -> bool
    {
        path.clear();
        for (DWORD capacity = 512; capacity <= 32 * 1024; capacity *= 2)
        {
            std::vector<wchar_t> buffer(capacity, L'\0');
            DWORD length = capacity;
            if (QueryFullProcessImageNameW(process, 0, buffer.data(), &length))
            {
                path.assign(buffer.data(), length);
                return true;
            }
            win32_error = GetLastError();
            if (win32_error != ERROR_INSUFFICIENT_BUFFER)
            {
                return false;
            }
        }
        win32_error = ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    auto query_creation_filetime(HANDLE process, std::uint64_t& creation, DWORD& win32_error) -> bool
    {
        FILETIME created{};
        FILETIME exited{};
        FILETIME kernel{};
        FILETIME user{};
        if (!GetProcessTimes(process, &created, &exited, &kernel, &user))
        {
            win32_error = GetLastError();
            return false;
        }
        ULARGE_INTEGER value{};
        value.LowPart = created.dwLowDateTime;
        value.HighPart = created.dwHighDateTime;
        creation = value.QuadPart;
        return true;
    }

    auto remote_module_base(DWORD pid, const std::wstring& expected_path, DWORD& win32_error) -> std::uintptr_t
    {
        win32_error = ERROR_MOD_NOT_FOUND;
        for (int attempt = 0; attempt < ModuleLookupAttempts; ++attempt)
        {
            HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snapshot == INVALID_HANDLE_VALUE)
            {
                win32_error = GetLastError();
                if (win32_error == ERROR_BAD_LENGTH)
                {
                    Sleep(ModuleLookupRetryMs);
                    continue;
                }
                return 0;
            }
            MODULEENTRY32W entry{};
            entry.dwSize = sizeof(entry);
            if (Module32FirstW(snapshot, &entry))
            {
                do
                {
                    const std::wstring module_path = normalized_path(entry.szExePath);
                    if (same_path(module_path, expected_path))
                    {
                        const auto base = reinterpret_cast<std::uintptr_t>(entry.modBaseAddr);
                        CloseHandle(snapshot);
                        return base;
                    }
                } while (Module32NextW(snapshot, &entry));
                win32_error = ERROR_MOD_NOT_FOUND;
            }
            else
            {
                win32_error = GetLastError();
            }
            CloseHandle(snapshot);
            Sleep(ModuleLookupRetryMs);
        }
        return 0;
    }

    // LoadLibraryW can be forwarded from kernel32 into KernelBase. Resolve the
    // local module that actually owns its code, then use that same module's RVA
    // in the target. This only resolves the system API used for this attempt;
    // the injector never searches for an existing bridge or loader.
    auto remote_load_library_w(DWORD pid, DWORD& win32_error) -> LPTHREAD_START_ROUTINE
    {
        const HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
        const auto local_load_library = kernel32 ? GetProcAddress(kernel32, "LoadLibraryW") : nullptr;
        if (!local_load_library)
        {
            win32_error = GetLastError();
            return nullptr;
        }

        HMODULE owner = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                reinterpret_cast<LPCWSTR>(local_load_library),
                                &owner) ||
            !owner)
        {
            win32_error = GetLastError();
            return nullptr;
        }

        wchar_t owner_path_buffer[MAX_PATH]{};
        if (GetModuleFileNameW(owner, owner_path_buffer, MAX_PATH) == 0)
        {
            win32_error = GetLastError();
            return nullptr;
        }
        const std::wstring owner_path = normalized_path(owner_path_buffer);
        const auto* owner_base = reinterpret_cast<const std::uint8_t*>(owner);
        const auto* function_address = reinterpret_cast<const std::uint8_t*>(local_load_library);
        if (owner_path.empty() || function_address < owner_base)
        {
            win32_error = ERROR_INVALID_ADDRESS;
            return nullptr;
        }
        const std::uintptr_t rva = static_cast<std::uintptr_t>(function_address - owner_base);
        const std::uintptr_t target_base = remote_module_base(pid, owner_path, win32_error);
        if (!target_base)
        {
            return nullptr;
        }
        return reinterpret_cast<LPTHREAD_START_ROUTINE>(target_base + rva);
    }

    auto local_export_rva(const std::wstring& dll_path, const char* export_name, DWORD& win32_error) -> std::uintptr_t
    {
        HMODULE local = LoadLibraryExW(dll_path.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        if (!local)
        {
            win32_error = GetLastError();
            return 0;
        }
        const auto* export_address = reinterpret_cast<const std::uint8_t*>(GetProcAddress(local, export_name));
        const auto* base = reinterpret_cast<const std::uint8_t*>(local);
        const auto rva = export_address ? static_cast<std::uintptr_t>(export_address - base) : 0;
        if (!rva)
        {
            win32_error = GetLastError();
        }
        // This releases only the injector's local DONT_RESOLVE_DLL_REFERENCES inspection
        // mapping. It is never a target-process bridge unload.
        FreeLibrary(local);
        return rva;
    }

    enum class RemoteWaitResult
    {
        Finished,
        Indeterminate,
    };

    auto wait_remote_thread(HANDLE thread, DWORD timeout_ms, DWORD& remote_exit, DWORD& win32_error) -> RemoteWaitResult
    {
        const DWORD waited = WaitForSingleObject(thread, timeout_ms);
        if (waited != WAIT_OBJECT_0)
        {
            win32_error = waited == WAIT_FAILED ? GetLastError() : WAIT_TIMEOUT;
            return RemoteWaitResult::Indeterminate;
        }
        remote_exit = 0;
        if (!GetExitCodeThread(thread, &remote_exit))
        {
            win32_error = GetLastError();
        }
        return RemoteWaitResult::Finished;
    }

    auto write_remote(HANDLE process, LPVOID remote, const void* local, SIZE_T bytes, DWORD& win32_error) -> bool
    {
        SIZE_T written = 0;
        if (!WriteProcessMemory(process, remote, local, bytes, &written) || written != bytes)
        {
            win32_error = GetLastError();
            return false;
        }
        return true;
    }

    auto all_zero(const std::uint8_t* bytes, std::size_t count) -> bool
    {
        std::uint8_t combined = 0;
        for (std::size_t index = 0; index < count; ++index)
        {
            combined |= bytes[index];
        }
        return combined == 0;
    }

    auto equal_bytes(const std::uint8_t* left, const std::uint8_t* right, std::size_t count) -> bool
    {
        std::uint8_t different = 0;
        for (std::size_t index = 0; index < count; ++index)
        {
            different |= static_cast<std::uint8_t>(left[index] ^ right[index]);
        }
        return different == 0;
    }

    auto sha256_file(const std::wstring& path, std::uint8_t (&hash)[32], DWORD& win32_error) -> bool
    {
        std::memset(hash, 0, sizeof(hash));
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            win32_error = GetLastError();
            return false;
        }

        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash_handle = nullptr;
        DWORD object_length = 0;
        DWORD hash_length = 0;
        DWORD result_length = 0;
        std::vector<std::uint8_t> hash_object{};
        const auto cleanup = [&]() {
            if (hash_handle)
            {
                BCryptDestroyHash(hash_handle);
            }
            if (algorithm)
            {
                BCryptCloseAlgorithmProvider(algorithm, 0);
            }
            CloseHandle(file);
        };

        NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
        if (!BCRYPT_SUCCESS(status))
        {
            cleanup();
            win32_error = ERROR_GEN_FAILURE;
            return false;
        }
        status = BCryptGetProperty(algorithm,
                                   BCRYPT_OBJECT_LENGTH,
                                   reinterpret_cast<PUCHAR>(&object_length),
                                   sizeof(object_length),
                                   &result_length,
                                   0);
        if (!BCRYPT_SUCCESS(status) || object_length == 0)
        {
            cleanup();
            win32_error = ERROR_GEN_FAILURE;
            return false;
        }
        status = BCryptGetProperty(algorithm,
                                   BCRYPT_HASH_LENGTH,
                                   reinterpret_cast<PUCHAR>(&hash_length),
                                   sizeof(hash_length),
                                   &result_length,
                                   0);
        if (!BCRYPT_SUCCESS(status) || hash_length != sizeof(hash))
        {
            cleanup();
            win32_error = ERROR_GEN_FAILURE;
            return false;
        }
        hash_object.resize(object_length);
        status = BCryptCreateHash(algorithm,
                                  &hash_handle,
                                  hash_object.data(),
                                  static_cast<ULONG>(hash_object.size()),
                                  nullptr,
                                  0,
                                  0);
        if (!BCRYPT_SUCCESS(status))
        {
            cleanup();
            win32_error = ERROR_GEN_FAILURE;
            return false;
        }

        std::uint8_t buffer[64 * 1024]{};
        for (;;)
        {
            DWORD read = 0;
            if (!ReadFile(file, buffer, sizeof(buffer), &read, nullptr))
            {
                win32_error = GetLastError();
                cleanup();
                return false;
            }
            if (read == 0)
            {
                break;
            }
            status = BCryptHashData(hash_handle, buffer, read, 0);
            if (!BCRYPT_SUCCESS(status))
            {
                cleanup();
                win32_error = ERROR_GEN_FAILURE;
                return false;
            }
        }
        status = BCryptFinishHash(hash_handle, hash, sizeof(hash), 0);
        if (!BCRYPT_SUCCESS(status))
        {
            cleanup();
            win32_error = ERROR_GEN_FAILURE;
            return false;
        }
        cleanup();
        return true;
    }

    enum class ArchitectureValidation
    {
        Compatible,
        Mismatch,
        Error,
    };

    auto validate_target_architecture(HANDLE target, DWORD& win32_error) -> ArchitectureValidation
    {
        using IsWow64Process2Fn = BOOL(WINAPI*)(HANDLE, USHORT*, USHORT*);
        const auto kernel32 = GetModuleHandleW(L"kernel32.dll");
        const auto is_wow64_process2 = kernel32
                                           ? reinterpret_cast<IsWow64Process2Fn>(GetProcAddress(kernel32, "IsWow64Process2"))
                                           : nullptr;
        if (is_wow64_process2)
        {
            USHORT current_process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            USHORT current_native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            USHORT target_process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            USHORT target_native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            if (!is_wow64_process2(GetCurrentProcess(), &current_process_machine, &current_native_machine) ||
                !is_wow64_process2(target, &target_process_machine, &target_native_machine))
            {
                win32_error = GetLastError();
                return ArchitectureValidation::Error;
            }
            return current_process_machine == target_process_machine && current_native_machine == target_native_machine
                       ? ArchitectureValidation::Compatible
                       : ArchitectureValidation::Mismatch;
        }

        BOOL current_wow64 = FALSE;
        BOOL target_wow64 = FALSE;
        if (!IsWow64Process(GetCurrentProcess(), &current_wow64) || !IsWow64Process(target, &target_wow64))
        {
            win32_error = GetLastError();
            return ArchitectureValidation::Error;
        }
        return current_wow64 == target_wow64 ? ArchitectureValidation::Compatible : ArchitectureValidation::Mismatch;
    }

    auto valid_start_input(const BridgeStartBlockV1& block, DWORD pid) -> bool
    {
        return block.magic == BridgeStartMagicV1 &&
               block.size == sizeof(BridgeStartBlockV1) &&
               block.abi == BridgeStartAbiV1 &&
               block.pid == pid &&
               block.requested_port == 0 &&
               block.result_state == BRIDGE_START_UNINITIALIZED &&
               block.bound_port == 0 &&
               block.protocol == BridgeBootstrapProtocolV1 &&
               block.win32_error == 0 &&
               block.winsock_error == 0 &&
               block.reserved0 == 0 &&
               block.reserved1 == 0 &&
               !all_zero(block.token, sizeof(block.token));
    }

    auto returned_start_block_matches(const BridgeStartBlockV1& expected, const BridgeStartBlockV1& actual) -> bool
    {
        return actual.magic == BridgeStartMagicV1 &&
               actual.size == sizeof(BridgeStartBlockV1) &&
               actual.abi == BridgeStartAbiV1 &&
               actual.pid == expected.pid &&
               actual.protocol == BridgeBootstrapProtocolV1 &&
               equal_bytes(actual.instance_guid, expected.instance_guid, sizeof(expected.instance_guid)) &&
               equal_bytes(actual.token, expected.token, sizeof(expected.token)) &&
               equal_bytes(actual.sha256, expected.sha256, sizeof(expected.sha256));
    }

    auto read_start_block(BridgeStartBlockV1& block) -> bool
    {
        if (_setmode(_fileno(stdin), _O_BINARY) == -1)
        {
            return false;
        }
        std::cin.read(reinterpret_cast<char*>(&block), sizeof(block));
        return std::cin.gcount() == static_cast<std::streamsize>(sizeof(block));
    }

    auto run_direct(DWORD pid,
                    std::uint64_t expected_creation,
                    const std::wstring& expected_executable,
                    const std::wstring& bridge_path,
                    const BridgeStartBlockV1& input) -> int
    {
        DWORD win32_error = 0;
        if (!valid_start_input(input, pid))
        {
            write_result(false, "failed", pid, &input, 0, ERROR_INVALID_DATA, 0, "invalid_start_block");
            return 2;
        }

        std::uint8_t staged_bridge_hash[32]{};
        if (!sha256_file(bridge_path, staged_bridge_hash, win32_error))
        {
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "bridge_sha256_failed");
            return 2;
        }
        if (!equal_bytes(staged_bridge_hash, input.sha256, sizeof(staged_bridge_hash)))
        {
            write_result(false, "failed", pid, &input, 0, ERROR_CRC, 0, "bridge_hash_mismatch");
            return 2;
        }

        HANDLE process = OpenProcess(PROCESS_CREATE_THREAD |
                                         PROCESS_QUERY_LIMITED_INFORMATION |
                                         PROCESS_VM_OPERATION |
                                         PROCESS_VM_WRITE |
                                         PROCESS_VM_READ,
                                     FALSE,
                                     pid);
        if (!process)
        {
            win32_error = GetLastError();
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "open_process_failed");
            return 3;
        }

        const auto architecture = validate_target_architecture(process, win32_error);
        if (architecture != ArchitectureValidation::Compatible)
        {
            CloseHandle(process);
            write_result(false,
                         "failed",
                         pid,
                         &input,
                         0,
                         architecture == ArchitectureValidation::Mismatch ? ERROR_BAD_EXE_FORMAT : win32_error,
                         0,
                         architecture == ArchitectureValidation::Mismatch ? "architecture_mismatch" : "architecture_validation_failed");
            return 3;
        }

        std::uint64_t actual_creation = 0;
        std::wstring actual_executable{};
        if (!query_creation_filetime(process, actual_creation, win32_error) ||
            !query_process_image_path(process, actual_executable, win32_error))
        {
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "target_identity_query_failed");
            return 4;
        }
        if (actual_creation != expected_creation || !same_path(actual_executable, expected_executable))
        {
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, ERROR_INVALID_PARAMETER, 0, "target_identity_mismatch");
            return 4;
        }

        const SIZE_T path_bytes = (bridge_path.size() + 1) * sizeof(wchar_t);
        LPVOID remote_path = VirtualAllocEx(process, nullptr, path_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!remote_path)
        {
            win32_error = GetLastError();
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_path_alloc_failed");
            return 5;
        }
        if (!write_remote(process, remote_path, bridge_path.c_str(), path_bytes, win32_error))
        {
            VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_path_write_failed");
            return 5;
        }

        const auto load_library = remote_load_library_w(pid, win32_error);
        if (!load_library)
        {
            VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_loadlibrary_resolution_failed");
            return 6;
        }

        HANDLE load_thread = CreateRemoteThread(process, nullptr, 0, load_library, remote_path, 0, nullptr);
        if (!load_thread)
        {
            win32_error = GetLastError();
            VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_load_thread_failed");
            return 6;
        }

        DWORD ignored_load_exit = 0;
        if (wait_remote_thread(load_thread, RemoteLoadTimeoutMs, ignored_load_exit, win32_error) == RemoteWaitResult::Indeterminate)
        {
            CloseHandle(load_thread);
            CloseHandle(process);
            // Do not free remote_path: the target LoadLibraryW thread may still read it.
            write_result(false, "indeterminate_timeout", pid, &input, 0, win32_error, 0, "remote_load_thread_not_finished");
            return 10;
        }
        CloseHandle(load_thread);
        VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);

        const auto remote_base = remote_module_base(pid, bridge_path, win32_error);
        if (!remote_base)
        {
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "exact_bridge_module_not_found");
            return 7;
        }
        const auto start_rva = local_export_rva(bridge_path, "BridgeStartV1", win32_error);
        if (!start_rva)
        {
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "bridge_start_export_not_found");
            return 8;
        }

        LPVOID remote_block = VirtualAllocEx(process, nullptr, sizeof(input), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!remote_block)
        {
            win32_error = GetLastError();
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_start_block_alloc_failed");
            return 8;
        }
        if (!write_remote(process, remote_block, &input, sizeof(input), win32_error))
        {
            VirtualFreeEx(process, remote_block, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_start_block_write_failed");
            return 8;
        }

        const auto remote_start = reinterpret_cast<LPTHREAD_START_ROUTINE>(remote_base + start_rva);
        HANDLE start_thread = CreateRemoteThread(process, nullptr, 0, remote_start, remote_block, 0, nullptr);
        if (!start_thread)
        {
            win32_error = GetLastError();
            VirtualFreeEx(process, remote_block, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_start_thread_failed");
            return 8;
        }

        DWORD start_exit = 0;
        if (wait_remote_thread(start_thread, RemoteStartTimeoutMs, start_exit, win32_error) == RemoteWaitResult::Indeterminate)
        {
            CloseHandle(start_thread);
            CloseHandle(process);
            // Do not free remote_block: BridgeStartV1 may still be copying or writing it.
            write_result(false, "indeterminate_timeout", pid, &input, 0, win32_error, 0, "remote_start_thread_not_finished");
            return 10;
        }
        CloseHandle(start_thread);

        BridgeStartBlockV1 returned{};
        SIZE_T read = 0;
        const bool read_ok = ReadProcessMemory(process, remote_block, &returned, sizeof(returned), &read) && read == sizeof(returned);
        if (!read_ok)
        {
            win32_error = GetLastError();
            VirtualFreeEx(process, remote_block, 0, MEM_RELEASE);
            CloseHandle(process);
            write_result(false, "failed", pid, &input, 0, win32_error, 0, "remote_start_result_read_failed");
            return 9;
        }
        VirtualFreeEx(process, remote_block, 0, MEM_RELEASE);
        CloseHandle(process);

        if (!returned_start_block_matches(input, returned))
        {
            write_result(false,
                         "failed",
                         pid,
                         &input,
                         0,
                         ERROR_INVALID_DATA,
                         returned.winsock_error,
                         "remote_start_result_invalid");
            return 9;
        }
        if (returned.result_state != BRIDGE_START_LISTENING || returned.bound_port == 0 || returned.bound_port > 65535 || start_exit != ERROR_SUCCESS)
        {
            write_result(false,
                         "failed",
                         pid,
                         &input,
                         returned.bound_port,
                         returned.win32_error,
                         returned.winsock_error,
                         "bridge_start_failed");
            return 9;
        }

        write_result(true,
                     "listening",
                     pid,
                     &returned,
                     returned.bound_port,
                     returned.win32_error,
                     returned.winsock_error,
                     "bridge_listening");
        return 0;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--self-test")
    {
        BridgeStartBlockV1 block{};
        block.magic = BridgeStartMagicV1;
        block.size = sizeof(block);
        block.abi = BridgeStartAbiV1;
        block.protocol = BridgeBootstrapProtocolV1;
        write_result(true, "self_test", 0, &block, 0, 0, 0, "direct_injector_abi_ready");
        return 0;
    }

    if (argc != 6 || std::wstring(argv[1]) != L"--direct")
    {
        write_result(false, "failed", 0, nullptr, 0, ERROR_INVALID_PARAMETER, 0,
                     "usage: runtime-injector.exe --direct <pid> <creation-filetime-utc> <expected-exe-path> <bridge-path>");
        return 64;
    }

    std::uint32_t pid = 0;
    std::uint64_t creation = 0;
    if (!parse_u32(argv[2], pid) || !parse_u64(argv[3], creation))
    {
        write_result(false, "failed", 0, nullptr, 0, ERROR_INVALID_PARAMETER, 0, "invalid_target_identity_arguments");
        return 64;
    }
    const std::wstring expected_executable = normalized_path(argv[4]);
    const std::wstring bridge_path = normalized_path(argv[5]);
    if (expected_executable.empty() || bridge_path.empty() || GetFileAttributesW(bridge_path.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        write_result(false, "failed", pid, nullptr, 0, ERROR_FILE_NOT_FOUND, 0, "target_or_bridge_path_invalid");
        return 64;
    }

    BridgeStartBlockV1 input{};
    if (!read_start_block(input))
    {
        write_result(false, "failed", pid, nullptr, 0, ERROR_INVALID_DATA, 0, "expected_128_byte_start_block_on_stdin");
        return 64;
    }
    return run_direct(pid, creation, expected_executable, bridge_path, input);
}
