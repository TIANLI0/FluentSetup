#include "Common/json.h"

#include "Common/text.h"

#include <unordered_set>
#include <charconv>
#include <cmath>
#include <cstdio>

namespace fsetup::json {

namespace {

const Value kNull;
const std::string kEmptyString;
const Array kEmptyArray;
const Object kEmptyObject;

class Parser {
public:
    Parser(std::string_view text, const ParseLimits& limits) : p_(text.data()), end_(text.data() + text.size()), limits_(limits) {}

    bool Run(Value& out, std::string* error) {
        // Tolerate a UTF-8 BOM.
        if (end_ - p_ >= 3 && static_cast<unsigned char>(p_[0]) == 0xEF && static_cast<unsigned char>(p_[1]) == 0xBB &&
            static_cast<unsigned char>(p_[2]) == 0xBF) {
            p_ += 3;
        }
        begin_ = p_;
        if (!ParseValue(out, 0)) {
            if (error) *error = error_ + " at offset " + std::to_string(p_ - begin_);
            return false;
        }
        SkipWs();
        if (p_ != end_) {
            if (error) *error = "trailing characters at offset " + std::to_string(p_ - begin_);
            return false;
        }
        return true;
    }

private:
    bool Fail(const char* message) {
        if (error_.empty()) error_ = message;
        return false;
    }

    void SkipWs() {
        while (p_ < end_ && (*p_ == ' ' || *p_ == '\t' || *p_ == '\n' || *p_ == '\r')) ++p_;
    }

    bool Literal(std::string_view word) {
        if (static_cast<size_t>(end_ - p_) < word.size() || std::string_view(p_, word.size()) != word) {
            return Fail("invalid literal");
        }
        p_ += word.size();
        return true;
    }

    bool ParseValue(Value& out, size_t depth) {
        if (depth > limits_.maxDepth) return Fail("nesting too deep");
        SkipWs();
        if (p_ >= end_) return Fail("unexpected end of input");
        switch (*p_) {
            case '{': return ParseObject(out, depth);
            case '[': return ParseArray(out, depth);
            case '"': {
                std::string s;
                if (!ParseString(s)) return false;
                out = Value(std::move(s));
                return true;
            }
            case 't':
                if (!Literal("true")) return false;
                out = Value(true);
                return true;
            case 'f':
                if (!Literal("false")) return false;
                out = Value(false);
                return true;
            case 'n':
                if (!Literal("null")) return false;
                out = Value();
                return true;
            default: return ParseNumber(out);
        }
    }

    bool ParseNumber(Value& out) {
        const char* start = p_;
        if (p_ < end_ && *p_ == '-') ++p_;
        if (p_ >= end_ || *p_ < '0' || *p_ > '9') return Fail("invalid number");
        bool integral = true;
        while (p_ < end_ && *p_ >= '0' && *p_ <= '9') ++p_;
        if (p_ < end_ && *p_ == '.') {
            integral = false;
            ++p_;
            if (p_ >= end_ || *p_ < '0' || *p_ > '9') return Fail("invalid number");
            while (p_ < end_ && *p_ >= '0' && *p_ <= '9') ++p_;
        }
        if (p_ < end_ && (*p_ == 'e' || *p_ == 'E')) {
            integral = false;
            ++p_;
            if (p_ < end_ && (*p_ == '+' || *p_ == '-')) ++p_;
            if (p_ >= end_ || *p_ < '0' || *p_ > '9') return Fail("invalid number");
            while (p_ < end_ && *p_ >= '0' && *p_ <= '9') ++p_;
        }
        if (integral) {
            int64_t i = 0;
            const auto r = std::from_chars(start, p_, i);
            if (r.ec == std::errc() && r.ptr == p_) {
                out = Value(i);
                return true;
            }
            // Fits uint64 but not int64 -> keep as unsigned bit pattern is unsafe; fall through to double.
        }
        double d = 0;
        const auto r = std::from_chars(start, p_, d);
        if (r.ec != std::errc() || r.ptr != p_ || !std::isfinite(d)) return Fail("number out of range");
        out = Value(d);
        return true;
    }

    static void AppendUtf8(std::string& s, uint32_t cp) {
        if (cp < 0x80) {
            s.push_back(static_cast<char>(cp));
        } else if (cp < 0x800) {
            s.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            s.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            s.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }

    bool Hex4(uint32_t& out) {
        if (end_ - p_ < 4) return Fail("truncated \\u escape");
        out = 0;
        for (int i = 0; i < 4; ++i) {
            const char c = *p_++;
            out <<= 4;
            if (c >= '0' && c <= '9') out |= static_cast<uint32_t>(c - '0');
            else if (c >= 'a' && c <= 'f') out |= static_cast<uint32_t>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') out |= static_cast<uint32_t>(c - 'A' + 10);
            else return Fail("invalid \\u escape");
        }
        return true;
    }

    bool ParseString(std::string& out) {
        ++p_;  // opening quote
        for (;;) {
            // Fast path: copy a run of ordinary bytes.
            const char* run = p_;
            while (p_ < end_ && *p_ != '"' && *p_ != '\\' && static_cast<unsigned char>(*p_) >= 0x20) ++p_;
            out.append(run, p_);
            if (p_ >= end_) return Fail("unterminated string");
            const char c = *p_++;
            if (c == '"') return true;
            if (c != '\\') return Fail("control character in string");
            if (p_ >= end_) return Fail("unterminated escape");
            const char e = *p_++;
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    uint32_t cp = 0;
                    if (!Hex4(cp)) return false;
                    if (cp >= 0xD800 && cp <= 0xDBFF) {
                        if (end_ - p_ < 2 || p_[0] != '\\' || p_[1] != 'u') return Fail("lone surrogate");
                        p_ += 2;
                        uint32_t low = 0;
                        if (!Hex4(low)) return false;
                        if (low < 0xDC00 || low > 0xDFFF) return Fail("invalid surrogate pair");
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
                        return Fail("lone surrogate");
                    }
                    AppendUtf8(out, cp);
                    break;
                }
                default: return Fail("invalid escape");
            }
        }
    }

    bool ParseArray(Value& out, size_t depth) {
        ++p_;
        Array arr;
        SkipWs();
        if (p_ < end_ && *p_ == ']') {
            ++p_;
            out = Value(std::move(arr));
            return true;
        }
        for (;;) {
            Value v;
            if (!ParseValue(v, depth + 1)) return false;
            arr.push_back(std::move(v));
            SkipWs();
            if (p_ >= end_) return Fail("unterminated array");
            if (*p_ == ',') {
                ++p_;
                continue;
            }
            if (*p_ == ']') {
                ++p_;
                break;
            }
            return Fail("expected ',' or ']'");
        }
        out = Value(std::move(arr));
        return true;
    }

    bool ParseObject(Value& out, size_t depth) {
        ++p_;
        Object obj;
        std::unordered_set<std::string> keys;  // only filled for large objects
        SkipWs();
        if (p_ < end_ && *p_ == '}') {
            ++p_;
            out = Value(std::move(obj));
            return true;
        }
        for (;;) {
            SkipWs();
            if (p_ >= end_ || *p_ != '"') return Fail("expected object key");
            std::string key;
            if (!ParseString(key)) return false;
            SkipWs();
            if (p_ >= end_ || *p_ != ':') return Fail("expected ':'");
            ++p_;
            Value v;
            if (!ParseValue(v, depth + 1)) return false;
            // Duplicate keys are ambiguous (readers disagree on which one wins): refuse them.
            if (obj.size() < 32) {
                for (const auto& m : obj) {
                    if (m.first == key) return Fail("duplicate object key");
                }
            } else {
                if (keys.empty()) {
                    for (const auto& m : obj) keys.insert(m.first);
                }
                if (!keys.insert(key).second) return Fail("duplicate object key");
            }
            obj.emplace_back(std::move(key), std::move(v));
            SkipWs();
            if (p_ >= end_) return Fail("unterminated object");
            if (*p_ == ',') {
                ++p_;
                continue;
            }
            if (*p_ == '}') {
                ++p_;
                break;
            }
            return Fail("expected ',' or '}'");
        }
        out = Value(std::move(obj));
        return true;
    }

    const char* p_;
    const char* end_;
    const char* begin_ = nullptr;
    ParseLimits limits_;
    std::string error_;
};

void AppendEscaped(std::string& out, std::string_view s) {
    out.push_back('"');
    for (const char ch : s) {
        const auto c = static_cast<unsigned char>(ch);
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out.push_back(ch);
                }
        }
    }
    out.push_back('"');
}

void NewLine(std::string& out, int indent, int level) {
    if (indent < 0) return;
    out.push_back('\n');
    out.append(static_cast<size_t>(indent) * static_cast<size_t>(level), ' ');
}

}  // namespace

void SerializeTo(const Value& v, std::string& out, int indent, int level) {
    switch (v.type_) {
        case Type::Null: out += "null"; break;
        case Type::Bool: out += v.bool_ ? "true" : "false"; break;
        case Type::Number: {
            char buf[40];
            if (v.isInt_) {
                const auto r = std::to_chars(buf, buf + sizeof(buf), v.int_);
                out.append(buf, r.ptr);
            } else {
                const auto r = std::to_chars(buf, buf + sizeof(buf), v.num_);
                out.append(buf, r.ptr);
            }
            break;
        }
        case Type::String: AppendEscaped(out, v.str_); break;
        case Type::Array: {
            const Array& a = *v.arr_;
            if (a.empty()) {
                out += "[]";
                break;
            }
            out.push_back('[');
            for (size_t i = 0; i < a.size(); ++i) {
                if (i) out.push_back(',');
                NewLine(out, indent, level + 1);
                SerializeTo(a[i], out, indent, level + 1);
            }
            NewLine(out, indent, level);
            out.push_back(']');
            break;
        }
        case Type::Object: {
            const Object& o = *v.obj_;
            if (o.empty()) {
                out += "{}";
                break;
            }
            out.push_back('{');
            for (size_t i = 0; i < o.size(); ++i) {
                if (i) out.push_back(',');
                NewLine(out, indent, level + 1);
                AppendEscaped(out, o[i].first);
                out.push_back(':');
                if (indent >= 0) out.push_back(' ');
                SerializeTo(o[i].second, out, indent, level + 1);
            }
            NewLine(out, indent, level);
            out.push_back('}');
            break;
        }
    }
}

Value::Value(std::wstring_view s) : type_(Type::String), str_(WideToUtf8(s)) {}

const std::string& Value::asString() const noexcept { return isString() ? str_ : kEmptyString; }

std::wstring Value::asWide() const { return isString() ? Utf8ToWide(str_) : std::wstring(); }

const Array& Value::array() const noexcept { return isArray() && arr_ ? *arr_ : kEmptyArray; }

const Object& Value::object() const noexcept { return isObject() && obj_ ? *obj_ : kEmptyObject; }

const Value& Value::operator[](std::string_view key) const noexcept {
    if (isObject() && obj_) {
        for (const auto& m : *obj_) {
            if (m.first == key) return m.second;
        }
    }
    return kNull;
}

bool Value::has(std::string_view key) const noexcept {
    if (isObject() && obj_) {
        for (const auto& m : *obj_) {
            if (m.first == key) return true;
        }
    }
    return false;
}

Value& Value::set(std::string_view key, Value v) {
    if (!isObject() || !obj_) {
        *this = MakeObject();
    } else if (obj_.use_count() > 1) {
        obj_ = std::make_shared<Object>(*obj_);  // copy on write
    }
    for (auto& m : *obj_) {
        if (m.first == key) {
            m.second = std::move(v);
            return *this;
        }
    }
    obj_->emplace_back(std::string(key), std::move(v));
    return *this;
}

Value& Value::push(Value v) {
    if (!isArray() || !arr_) {
        *this = MakeArray();
    } else if (arr_.use_count() > 1) {
        arr_ = std::make_shared<Array>(*arr_);
    }
    arr_->push_back(std::move(v));
    return *this;
}

bool Parse(std::string_view text, Value& out, std::string* error, const ParseLimits& limits) {
    if (text.size() > limits.maxBytes) {
        if (error) *error = "document too large";
        return false;
    }
    Parser parser(text, limits);
    return parser.Run(out, error);
}

std::string Serialize(const Value& v, int indent) {
    std::string out;
    SerializeTo(v, out, indent, 0);
    return out;
}

}  // namespace fsetup::json
