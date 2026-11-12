#include "test.h"

#include "Common/error.h"
#include "Common/log.h"

#include <windows.h>

int wmain(int argc, wchar_t** argv) {
    const wchar_t* filter = argc > 1 ? argv[1] : nullptr;
    int failed = 0, run = 0;
    for (const auto& c : ftest::Registry()) {
        if (filter) {
            wchar_t wide[256];
            MultiByteToWideChar(CP_UTF8, 0, c.name, -1, wide, 256);
            if (!wcsstr(wide, filter)) continue;
        }
        ++run;
        try {
            c.body();
            std::printf("  [PASS] %s\n", c.name);
        } catch (const ftest::AssertionFailed& e) {
            ++failed;
            std::printf("  [FAIL] %s\n         %s\n", c.name, e.message.c_str());
        } catch (const fsetup::Failure& f) {
            ++failed;
            char detail[1024] = {};
            WideCharToMultiByte(CP_UTF8, 0, f.detail().c_str(), -1, detail, sizeof(detail), nullptr, nullptr);
            std::printf("  [FAIL] %s\n         unexpected Failure %u: %s\n", c.name, static_cast<unsigned>(f.code()), detail);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("  [FAIL] %s\n         exception: %s\n", c.name, e.what());
        }
    }
    std::printf("\n%d tests, %d checks, %d failed\n", run, ftest::Checks(), failed);
    return failed;
}
