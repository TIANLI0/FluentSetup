// Minimal test harness (no third-party dependency).
#pragma once

#include <cstdio>
#include <functional>
#include <string>
#include <vector>

namespace ftest {

struct Case {
    const char* name;
    std::function<void()> body;
};

inline std::vector<Case>& Registry() {
    static std::vector<Case> cases;
    return cases;
}

struct Registrar {
    Registrar(const char* name, std::function<void()> body) { Registry().push_back({name, std::move(body)}); }
};

struct AssertionFailed {
    std::string message;
};

inline int& Checks() {
    static int n = 0;
    return n;
}

}  // namespace ftest

#define FT_CONCAT2(a, b) a##b
#define FT_CONCAT(a, b) FT_CONCAT2(a, b)
#define TEST(name)                                                                              \
    static void FT_CONCAT(test_, name)();                                                       \
    static ftest::Registrar FT_CONCAT(reg_, name)(#name, &FT_CONCAT(test_, name));              \
    static void FT_CONCAT(test_, name)()

#define CHECK(expr)                                                                                       \
    do {                                                                                                  \
        ++ftest::Checks();                                                                                \
        if (!(expr)) throw ftest::AssertionFailed{std::string(__FILE__) + ":" + std::to_string(__LINE__) + \
                                                  ": CHECK(" #expr ") failed"};                           \
    } while (0)

#define CHECK_THROWS_CODE(expr, expected_code)                                                                  \
    do {                                                                                               \
        ++ftest::Checks();                                                                             \
        bool thrown_ = false;                                                                          \
        try {                                                                                          \
            expr;                                                                                      \
        } catch (const fsetup::Failure& f_) {                                                          \
            thrown_ = true;                                                                            \
            if (f_.code() != (expected_code)) {                                                                 \
                throw ftest::AssertionFailed{std::string(__FILE__) + ":" + std::to_string(__LINE__) +   \
                                             ": wrong error code for " #expr};                         \
            }                                                                                          \
        }                                                                                              \
        if (!thrown_) throw ftest::AssertionFailed{std::string(__FILE__) + ":" + std::to_string(__LINE__) + \
                                                   ": expected failure from " #expr};                  \
    } while (0)
