    auto bridge_sidecar_path(const wchar_t* suffix) -> std::wstring
    {
        wchar_t dll_path[MAX_PATH]{};
        if (g_module != nullptr && GetModuleFileNameW(g_module, dll_path, MAX_PATH) > 0)
        {
            std::wstring path = dll_path;
            path += suffix;
            return path;
        }
        return {};
    }

    auto read_small_text_file_w(const std::wstring& path, std::size_t max_bytes, std::string& text) -> bool
    {
        text.clear();
        if (path.empty() || max_bytes == 0)
        {
            return false;
        }
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > max_bytes)
        {
            CloseHandle(file);
            return false;
        }
        text.resize(static_cast<std::size_t>(size.QuadPart));
        DWORD read = 0;
        const auto ok = ReadFile(file, text.data(), static_cast<DWORD>(text.size()), &read, nullptr);
        CloseHandle(file);
        if (!ok || read != text.size())
        {
            text.clear();
            return false;
        }
        return true;
    }

    auto trim_ascii_whitespace(std::string text) -> std::string
    {
        while (!text.empty() && std::isspace(static_cast<unsigned char>(text.back())))
        {
            text.pop_back();
        }
        std::size_t first = 0;
        while (first < text.size() && std::isspace(static_cast<unsigned char>(text[first])))
        {
            ++first;
        }
        return first == 0 ? text : text.substr(first);
    }

    auto utf8_to_wstring(const std::string& text) -> std::wstring
    {
        if (text.empty())
        {
            return {};
        }
        const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
        if (size <= 0)
        {
            return {};
        }
        std::wstring wide(static_cast<std::size_t>(size), L'\0');
        const int written = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), wide.data(), size);
        if (written != size)
        {
            return {};
        }
        return wide;
    }

    auto wstring_to_utf8(const std::wstring& text) -> std::string
    {
        if (text.empty())
        {
            return {};
        }
        const int size = WideCharToMultiByte(CP_UTF8,
                                             WC_ERR_INVALID_CHARS,
                                             text.data(),
                                             static_cast<int>(text.size()),
                                             nullptr,
                                             0,
                                             nullptr,
                                             nullptr);
        if (size <= 0)
        {
            return {};
        }
        std::string utf8(static_cast<std::size_t>(size), '\0');
        const int written = WideCharToMultiByte(CP_UTF8,
                                                WC_ERR_INVALID_CHARS,
                                                text.data(),
                                                static_cast<int>(text.size()),
                                                utf8.data(),
                                                size,
                                                nullptr,
                                                nullptr);
        return written == size ? utf8 : std::string{};
    }

    auto bridge_progress_path() -> std::wstring
    {
        const auto configured_sidecar = bridge_sidecar_path(L".progress.path");
        std::string configured{};
        if (!configured_sidecar.empty() && read_small_text_file_w(configured_sidecar, 4096, configured))
        {
            if (configured.size() >= 3 &&
                static_cast<unsigned char>(configured[0]) == 0xef &&
                static_cast<unsigned char>(configured[1]) == 0xbb &&
                static_cast<unsigned char>(configured[2]) == 0xbf)
            {
                configured.erase(0, 3);
            }
            const auto wide = utf8_to_wstring(trim_ascii_whitespace(configured));
            if (!wide.empty())
            {
                return wide;
            }
        }
        return bridge_sidecar_path(L".progress.json");
    }

    auto write_bridge_progress(const std::string& stage,
                               const std::string& message,
                               int step,
                               int total_steps,
                               double elapsed_ms,
                               const std::string& extra = "") -> void
    {
        const auto path = bridge_progress_path();
        if (path.empty())
        {
            return;
        }
        const double progress = total_steps > 0 ? std::max(0.0, std::min(1.0, static_cast<double>(step) / static_cast<double>(total_steps))) : 0.0;
        std::string json = "{\"stage\":\"" + json_escape(stage) +
                           "\",\"message\":\"" + json_escape(message) +
                           "\",\"step\":" + std::to_string(step) +
                           ",\"total_steps\":" + std::to_string(total_steps) +
                           ",\"progress\":" + std::to_string(progress) +
                           ",\"elapsed_ms\":" + std::to_string(elapsed_ms);
        if (!extra.empty())
        {
            json += ",";
            json += extra;
        }
        json += "}\n";
        static std::atomic<std::uint64_t> temp_sequence{0};
        std::wstring temp_path;
        HANDLE file = INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            const auto sequence = temp_sequence.fetch_add(1, std::memory_order_relaxed) + 1;
            temp_path = path + L".tmp." +
                        std::to_wstring(GetCurrentProcessId()) + L"." +
                        std::to_wstring(sequence);
            file = CreateFileW(temp_path.c_str(),
                               GENERIC_WRITE,
                               0,
                               nullptr,
                               CREATE_NEW,
                               FILE_ATTRIBUTE_NORMAL,
                               nullptr);
            if (file != INVALID_HANDLE_VALUE)
            {
                break;
            }
            const auto error = GetLastError();
            if (error != ERROR_FILE_EXISTS && error != ERROR_ALREADY_EXISTS)
            {
                break;
            }
        }
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }
        DWORD written = 0;
        const bool write_ok =
            json.size() <= static_cast<std::size_t>(std::numeric_limits<DWORD>::max()) &&
            WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr) != FALSE &&
            written == json.size();
        const bool flush_ok = write_ok && FlushFileBuffers(file) != FALSE;
        const bool close_ok = CloseHandle(file) != FALSE;
        const bool replace_ok = flush_ok && close_ok &&
                                MoveFileExW(temp_path.c_str(),
                                            path.c_str(),
                                            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
        if (!replace_ok)
        {
            DeleteFileW(temp_path.c_str());
        }
    }

    auto clear_bridge_progress() -> void
    {
        const auto path = bridge_progress_path();
        if (!path.empty())
        {
            DeleteFileW(path.c_str());
        }
    }
