// Process-wide, thread-safe file logger. Logging never throws and never blocks installation.
#pragma once

#include "Common/win.h"

#include <string>
#include <string_view>

namespace fsetup::log {

enum class Level { Debug, Info, Warn, Error };

// Opens (appends to) the log file. Parent directories are created. Returns false when the file cannot be opened;
// logging then silently becomes a no-op. `tag` identifies the process role ("setup", "worker", "ui").
bool Open(const std::wstring& path, std::wstring_view tag);
void Close();
[[nodiscard]] std::wstring Path();

void Write(Level level, std::wstring_view message);

void Info(const wchar_t* fmt, ...);
void Warn(const wchar_t* fmt, ...);
void Error(const wchar_t* fmt, ...);
void Debug(const wchar_t* fmt, ...);

}  // namespace fsetup::log
