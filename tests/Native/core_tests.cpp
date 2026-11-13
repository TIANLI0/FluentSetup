// Unit tests for the native building blocks: path rules, versions, JSON, arguments, text helpers.
#include "test.h"

#include "Bootstrapper/args.h"
#include "Common/json.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Common/version.h"
#include "Install.Core/extensions.h"

#include <shellapi.h>

using namespace fsetup;

TEST(relative_paths_accept_normal_names) {
    std::wstring n;
    CHECK(path::ValidateRelative(L"bin\\app.exe", n) && n == L"bin\\app.exe");
    CHECK(path::ValidateRelative(L"bin/sub/app.dll", n) && n == L"bin\\sub\\app.dll");
    CHECK(path::ValidateRelative(L"中文 目录\\说明.txt", n));
    CHECK(path::ValidateRelative(L".hidden", n));
    CHECK(path::ValidateRelative(L"a..b", n));
}

TEST(relative_paths_reject_traversal_and_tricks) {
    std::wstring n;
    for (const wchar_t* bad : {L"..\\evil.dll", L"a\\..\\..\\evil", L"a\\.\\b", L"\\abs", L"/abs", L"C:\\x", L"C:x",
                               L"\\\\server\\share\\x", L"a\\\\b", L"a\\", L"file.txt:stream", L"con", L"NUL.txt",
                               L"com1", L"LPT9.log", L"a\\aux\\b", L"trailing.", L"trailing ", L" leading", L"a<b",
                               L"a|b", L"a?b", L"a*b", L"a\"b", L"", L"tab\there"}) {
        CHECK(!path::ValidateRelative(bad, n));
    }
    std::wstring deep;
    for (int i = 0; i < 70; ++i) deep += L"d\\";
    deep += L"f";
    CHECK(!path::ValidateRelative(deep, n));
    CHECK(!path::ValidateRelative(std::wstring(1100, L'a'), n));
    CHECK(!path::ValidateRelative(std::wstring(256, L'a'), n));
}

TEST(absolute_install_dirs) {
    std::wstring n;
    CHECK(path::ValidateAbsoluteSyntax(L"C:\\Apps\\My App", n) == path::Problem::None && n == L"C:\\Apps\\My App");
    CHECK(path::ValidateAbsoluteSyntax(L"c:/apps/x/", n) == path::Problem::None && n == L"C:\\apps\\x");
    CHECK(path::ValidateAbsoluteSyntax(L"\"C:\\Apps\\Quoted\"", n) == path::Problem::None);
    CHECK(path::ValidateAbsoluteSyntax(L"", n) == path::Problem::Empty);
    CHECK(path::ValidateAbsoluteSyntax(L"relative\\dir", n) == path::Problem::NotAbsolute);
    CHECK(path::ValidateAbsoluteSyntax(L"C:\\", n) == path::Problem::Root);
    CHECK(path::ValidateAbsoluteSyntax(L"\\\\server\\share\\app", n) == path::Problem::Network);
    CHECK(path::ValidateAbsoluteSyntax(L"\\\\?\\C:\\x", n) == path::Problem::InvalidChars);
    CHECK(path::ValidateAbsoluteSyntax(L"C:\\Apps\\..\\Windows", n) == path::Problem::InvalidChars);
    CHECK(path::ValidateAbsoluteSyntax(L"C:\\Apps\\CON", n) == path::Problem::ReservedName);
    CHECK(path::ValidateAbsoluteSyntax(L"C:\\" + std::wstring(240, L'a'), n) == path::Problem::TooLong);
}

TEST(path_containment) {
    CHECK(path::IsSameOrUnder(L"C:\\A\\B", L"C:\\A"));
    CHECK(path::IsSameOrUnder(L"c:\\a", L"C:\\A\\"));
    CHECK(!path::IsSameOrUnder(L"C:\\AB", L"C:\\A"));
    CHECK(!path::IsStrictlyUnder(L"C:\\A", L"C:\\A"));
    CHECK(path::IsStrictlyUnder(L"C:\\A\\x.exe", L"C:\\A"));
}

TEST(sanitize_names) {
    CHECK(path::SanitizeName(L"My: App?") == L"My_ App_");
    CHECK(path::SanitizeName(L"  ") == L"App");
    CHECK(path::SanitizeName(L"CON") == L"AppCON");
    CHECK(path::SanitizeName(L"Name...") == L"Name");
}

TEST(versions) {
    Version a, b;
    CHECK(Version::Parse(L"1.2.3", a) && a.parts[0] == 1 && a.parts[2] == 3);
    CHECK(Version::Parse(L"10.0.0.1", b));
    CHECK(Compare(a, b) < 0);
    CHECK(Version::Parse(L"2.0.0-beta.2", a) && Version::Parse(L"2.0.0-beta.10", b) && Compare(a, b) < 0);
    CHECK(Version::Parse(L"2.0.0", b) && Compare(a, b) < 0);  // pre-release < release
    CHECK(Version::Parse(L"2.0", a) && Compare(a, b) == 0);
    CHECK(Version::Parse(L"2.0.0-alpha", a) && Version::Parse(L"2.0.0-1", b) && Compare(b, a) < 0);  // numeric < alpha
    for (const wchar_t* bad : {L"", L"1.", L".1", L"1..2", L"1.2.3.4.5", L"a.b", L"1.2-", L"99999999999.1", L"1.2-b@d"}) {
        CHECK(!Version::Parse(bad, a));
    }
}

TEST(json_round_trip_and_unicode) {
    json::Value v;
    CHECK(json::Parse(R"({"a":1,"b":[true,false,null],"c":"x\u00e9\ud83d\ude00","d":-2.5e3,"e":{"f":"\"q\""}})", v));
    CHECK(v["a"].asInt() == 1);
    CHECK(v["b"].array().size() == 3 && v["b"].array()[0].asBool());
    CHECK(v["c"].asWide() == L"x\u00e9\U0001F600");
    CHECK(v["d"].asDouble() == -2500.0);
    CHECK(v["e"]["f"].asString() == "\"q\"");
    json::Value again;
    CHECK(json::Parse(json::Serialize(v, 2), again) && again["c"].asWide() == v["c"].asWide());
}

TEST(json_rejects_malformed_and_hostile_input) {
    json::Value v;
    for (const char* bad : {"", "{", "{\"a\":}", "[1,]", "{\"a\" 1}", "tru", "\"\\x\"", "\"\\ud800\"", "01x", "{}extra",
                            "\"unterminated", "[\"ctl\x01\"]", "1e999"}) {
        CHECK(!json::Parse(bad, v));
    }
    std::string deep(200, '[');
    deep += std::string(200, ']');
    CHECK(!json::Parse(deep, v));  // depth limit
    json::ParseLimits tiny;
    tiny.maxBytes = 4;
    CHECK(!json::Parse("[1,2,3]", v, nullptr, tiny));
}

TEST(json_copy_on_write) {
    json::Value a;
    a.set("x", 1);
    json::Value b = a;
    b.set("x", 2);
    CHECK(a["x"].asInt() == 1 && b["x"].asInt() == 2);
}

TEST(arguments_strict) {
    boot::Args a;
    std::wstring err;
    CHECK(boot::ParseArgs(L"setup.exe /quiet /dir \"C:\\My Apps\\X\" /scope machine /desktop-shortcut=no /lang zh-CN", a, err));
    CHECK(a.quiet && a.installDir == L"C:\\My Apps\\X" && a.machineScope && *a.machineScope && a.desktopShortcut && !*a.desktopShortcut);
    CHECK(a.language == L"zh-CN");
    boot::Args b;
    CHECK(boot::ParseArgs(L"setup.exe --ui=win32 -log C:\\logs\\x.log", b, err) && b.ui == boot::UiChoice::Win32 && b.logPath == L"C:\\logs\\x.log");
    boot::Args inno;
    CHECK(boot::ParseArgs(L"setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"C:\\Apps\\My App\" /TASKS=\"desktopicon,!autostart\" /RELAUNCH=1 /LANG=japanese", inno, err) &&
          inno.quiet && inno.installDir == L"C:\\Apps\\My App" && inno.relaunch && inno.language == L"ja-JP" && inno.tasks && inno.tasks->size() == 2);
    boot::Args nsis;
    CHECK(boot::ParseArgs(L"setup.exe /S /D=C:\\Program Files\\My App", nsis, err) && nsis.quiet && nsis.installDir == L"C:\\Program Files\\My App");
    for (const wchar_t* bad : {L"setup.exe /bogus", L"setup.exe /dir", L"setup.exe /scope everyone", L"setup.exe stray",
                               L"setup.exe /quiet=1", L"setup.exe /log relative.log", L"setup.exe /ui html",
                               L"setup.exe --worker", L"setup.exe --worker --pipe x --parent-pid abc",
                               L"setup.exe --pipe ..\\..\\evil --worker --parent-pid 5", L"setup.exe --ui-simulate rm-rf",
                               L"setup.exe /lang ..\\x", L"setup.exe --phase2 --origin-dir C:\\x"}) {
        boot::Args c;
        CHECK(!boot::ParseArgs(bad, c, err));
    }
}

TEST(arguments_update_switches) {
    std::wstring err;
    boot::Args check;
    CHECK(boot::ParseArgs(L"uninstall.exe /check-update", check, err) && check.checkUpdate && !check.installUpdate);
    boot::Args install;
    CHECK(boot::ParseArgs(L"uninstall.exe /install-update /passive /relaunch", install, err) && install.installUpdate && install.passive && install.relaunch);
    boot::Args quiet;
    CHECK(boot::ParseArgs(L"uninstall.exe --install-update --quiet", quiet, err) && quiet.installUpdate && quiet.quiet);
    for (const wchar_t* bad : {L"uninstall.exe /check-update /install-update", L"uninstall.exe /install-update /uninstall",
                               L"uninstall.exe /check-update=1"}) {
        boot::Args c;
        CHECK(!boot::ParseArgs(bad, c, err));
    }
}

TEST(arguments_modify_repair_inputs) {
    std::wstring err;
    boot::Args m;
    CHECK(boot::ParseArgs(L"uninstall.exe /modify /quiet /desktop-shortcut no /input server=https://x.example/?a=b "
                          L"/input=edition=pro --input \"note=two words\" /input empty=",
                          m, err));
    CHECK(m.modify && !m.repair && !m.uninstall && m.quiet && m.desktopShortcut && !*m.desktopShortcut);
    CHECK(m.inputs.size() == 4 && m.inputs["server"] == L"https://x.example/?a=b" && m.inputs["edition"] == L"pro");
    CHECK(m.inputs["note"] == L"two words" && m.inputs.count("empty") == 1 && m.inputs["empty"].empty());
    boot::Args again;  // the last value of a repeated id wins
    CHECK(boot::ParseArgs(L"setup.exe /input a=1 /input a=2", again, err) && again.inputs.size() == 1 && again.inputs["a"] == L"2");
    boot::Args r;
    CHECK(boot::ParseArgs(L"uninstall.exe --repair /quiet", r, err) && r.repair && !r.modify && r.quiet);
    boot::Args w;
    CHECK(boot::ParseArgs(L"setup.exe --worker --pipe FluentSetup-ab12 --parent-pid 5 /modify /lang en-US", w, err) && w.worker && w.modify);
    for (const wchar_t* bad : {L"uninstall.exe /modify /uninstall", L"uninstall.exe /repair /modify", L"uninstall.exe /repair /uninstall",
                               L"uninstall.exe /modify=yes", L"uninstall.exe /repair=1", L"uninstall.exe /modify /check-update",
                               L"uninstall.exe /repair /install-update", L"setup.exe --worker --pipe x --parent-pid 5 /repair",
                               L"setup.exe /input", L"setup.exe /input =x", L"setup.exe /input noequals", L"setup.exe /input bad.id=1",
                               L"setup.exe /input b\u00e4d=1", L"setup.exe /input 012345678901234567890123456789012=x",
                               L"setup.exe /input a=1 /input b=1 /input c=1 /input d=1 /input e=1 /input f=1 /input g=1 /input h=1 /input i=1"}) {
        boot::Args c;
        CHECK(!boot::ParseArgs(bad, c, err));
    }
}

TEST(input_values_checked) {
    install::InputDef server;
    server.id = "server";
    server.required = true;
    server.pattern = L"^https://[^ ]+$";
    CHECK(!install::CheckInputValue(server, L"").empty());
    CHECK(!install::CheckInputValue(server, L"http://example.test").empty());
    CHECK(!install::CheckInputValue(server, L"https://two words").empty());
    CHECK(install::CheckInputValue(server, L"https://example.test/api").empty());
    CHECK(!install::CheckInputValue(server, L"https://" + std::wstring(4096, L'a')).empty());  // longer than 4096

    install::InputDef port;  // optional: empty is fine, anything else must match
    port.id = "port";
    port.pattern = L"^[0-9]{1,5}$";
    CHECK(install::CheckInputValue(port, L"").empty());
    CHECK(install::CheckInputValue(port, L"8080").empty());
    CHECK(!install::CheckInputValue(port, L"80a").empty());
    CHECK(!install::CheckInputValue(port, L"123456").empty());

    install::InputDef edition;
    edition.id = "edition";
    edition.type = install::InputDef::Type::Choice;
    edition.choices = {{L"standard", {}}, {L"pro", {}}};
    CHECK(install::CheckInputValue(edition, L"pro").empty());
    CHECK(!install::CheckInputValue(edition, L"Pro").empty());  // values are exact
    CHECK(!install::CheckInputValue(edition, L"enterprise").empty());
    CHECK(install::CheckInputValue(edition, L"").empty());  // not required
    edition.required = true;
    CHECK(!install::CheckInputValue(edition, L"").empty());

    install::InputDef secret;  // password: no pattern, any characters
    secret.id = "token";
    secret.type = install::InputDef::Type::Password;
    secret.required = true;
    CHECK(install::CheckInputValue(secret, L"p@ss w\u00f6rd \"'<>").empty());
    CHECK(!install::CheckInputValue(secret, L"").empty());
}

TEST(argument_quoting_round_trips) {
    for (const std::wstring s : {L"plain", L"with space", L"trailing\\", L"quote\"inside", L"C:\\Program Files\\", L"a\\\\\"b"}) {
        int argc = 0;
        const std::wstring cmd = L"x.exe " + boot::QuoteArg(s);
        LPWSTR* argv = ::CommandLineToArgvW(cmd.c_str(), &argc);
        CHECK(argc == 2 && s == argv[1]);
        ::LocalFree(argv);
    }
}

TEST(text_helpers) {
    CHECK(FormatIndexed(L"{0} and {1} and {0}", {L"A", L"B"}) == L"A and B and A");
    CHECK(FormatIndexed(L"{5} missing", {L"A"}) == L" missing");
    CHECK(FormatBytes(512) == L"512 B");
    CHECK(FormatBytes(1536) == L"1.5 KB");
    std::vector<uint8_t> bytes;
    CHECK(FromHex("00ff10", bytes) && bytes.size() == 3 && bytes[1] == 0xFF);
    CHECK(!FromHex("0g", bytes) && !FromHex("abc", bytes));
    CHECK(Utf8ToWide(WideToUtf8(L"中文 ✓")) == L"中文 ✓");
}
