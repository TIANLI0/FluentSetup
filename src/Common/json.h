// Minimal, bounded JSON DOM. Used for the package manifest, the install record and IPC messages.
// Deliberately small: no third-party dependency, hard limits on depth and size, UTF-8 in and out.
#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace fsetup::json {

class Value;
using Array = std::vector<Value>;
using Member = std::pair<std::string, Value>;
using Object = std::vector<Member>;  // insertion ordered; lookups are linear (objects here are small)

enum class Type { Null, Bool, Number, String, Array, Object };

class Value {
public:
    Value() noexcept = default;
    Value(std::nullptr_t) noexcept {}
    Value(bool b) noexcept : type_(Type::Bool), bool_(b) {}
    Value(int v) noexcept : type_(Type::Number), num_(v), int_(v), isInt_(true) {}
    Value(unsigned v) noexcept : type_(Type::Number), num_(v), int_(v), isInt_(true) {}
    Value(int64_t v) noexcept : type_(Type::Number), num_(static_cast<double>(v)), int_(v), isInt_(true) {}
    Value(uint64_t v) noexcept
        : type_(Type::Number), num_(static_cast<double>(v)), int_(static_cast<int64_t>(v)), isInt_(true) {}
    Value(double v) noexcept : type_(Type::Number), num_(v), int_(static_cast<int64_t>(v)) {}
    Value(std::string s) : type_(Type::String), str_(std::move(s)) {}
    Value(std::string_view s) : type_(Type::String), str_(s) {}
    Value(const char* s) : type_(Type::String), str_(s) {}
    Value(std::wstring_view s);
    Value(const std::wstring& s) : Value(std::wstring_view(s)) {}
    Value(const wchar_t* s) : Value(std::wstring_view(s)) {}
    Value(Array a) : type_(Type::Array), arr_(std::make_shared<Array>(std::move(a))) {}
    Value(Object o) : type_(Type::Object), obj_(std::make_shared<Object>(std::move(o))) {}

    static Value MakeObject() { return Value(Object{}); }
    static Value MakeArray() { return Value(Array{}); }

    [[nodiscard]] Type type() const noexcept { return type_; }
    [[nodiscard]] bool isNull() const noexcept { return type_ == Type::Null; }
    [[nodiscard]] bool isBool() const noexcept { return type_ == Type::Bool; }
    [[nodiscard]] bool isNumber() const noexcept { return type_ == Type::Number; }
    [[nodiscard]] bool isString() const noexcept { return type_ == Type::String; }
    [[nodiscard]] bool isArray() const noexcept { return type_ == Type::Array; }
    [[nodiscard]] bool isObject() const noexcept { return type_ == Type::Object; }

    // Lenient accessors: return the fallback when the type does not match.
    [[nodiscard]] bool asBool(bool fallback = false) const noexcept { return isBool() ? bool_ : fallback; }
    [[nodiscard]] int64_t asInt(int64_t fallback = 0) const noexcept { return isNumber() ? int_ : fallback; }
    [[nodiscard]] uint64_t asUint(uint64_t fallback = 0) const noexcept {
        return isNumber() && int_ >= 0 ? static_cast<uint64_t>(int_) : fallback;
    }
    [[nodiscard]] double asDouble(double fallback = 0) const noexcept { return isNumber() ? num_ : fallback; }
    [[nodiscard]] const std::string& asString() const noexcept;
    [[nodiscard]] std::wstring asWide() const;

    [[nodiscard]] const Array& array() const noexcept;
    [[nodiscard]] const Object& object() const noexcept;

    // Object lookup; returns a shared Null value when missing or when this is not an object.
    [[nodiscard]] const Value& operator[](std::string_view key) const noexcept;
    [[nodiscard]] bool has(std::string_view key) const noexcept;

    // Mutation helpers (convert Null into the required container on first use).
    Value& set(std::string_view key, Value v);
    Value& push(Value v);

private:
    Type type_ = Type::Null;
    bool bool_ = false;
    double num_ = 0;
    int64_t int_ = 0;
    bool isInt_ = false;
    std::string str_;
    std::shared_ptr<Array> arr_;
    std::shared_ptr<Object> obj_;

    friend void SerializeTo(const Value&, std::string&, int, int);
};

struct ParseLimits {
    size_t maxDepth = 64;
    size_t maxBytes = 512u * 1024u * 1024u;
};

// Returns false and fills `error` on malformed input. Never throws on bad input.
bool Parse(std::string_view text, Value& out, std::string* error = nullptr, const ParseLimits& limits = {});

// indent < 0 -> compact single line.
std::string Serialize(const Value& v, int indent = -1);

}  // namespace fsetup::json
