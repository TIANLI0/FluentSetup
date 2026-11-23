// Unit tests for taking over older installations (install.legacyMsi) and update channels (update.channel, /channel,
// the feed's "channels"). Nothing here changes the system: the only Windows Installer calls are read-only queries for
// codes no product uses.
#include "test.h"

#include "Bootstrapper/args.h"
#include "Bootstrapper/feed.h"
#include "Common/json.h"
#include "Common/text.h"
#include "Install.Core/engine.h"
#include "Install.Core/extensions.h"
#include "Install.Core/installed.h"
#include "Install.Core/legacy_msi.h"

#include <objbase.h>

using namespace fsetup;

namespace {

json::Value Json(const std::string& text) {
    json::Value v;
    if (!json::Parse(text, v)) throw ftest::AssertionFailed{"bad test JSON: " + text};
    return v;
}

install::Extensions Ext(const std::string& install) {
    return install::ParseExtensions(Json(R"({"install":)" + install + "}"), [](const std::wstring&) { return true; });
}

std::wstring RandomGuid() {
    GUID g{};
    if (FAILED(::CoCreateGuid(&g))) throw ftest::AssertionFailed{"CoCreateGuid failed"};
    wchar_t text[40] = {};
    ::StringFromGUID2(g, text, ARRAYSIZE(text));
    return text;
}

const std::string kHash = std::string(64, 'a');

std::string Entry(const std::string& version) {
    return R"({"version":")" + version + R"(","url":"https://example.com/App-)" + version + R"(.exe","sha256":")" + kHash + R"(","size":10})";
}

}  // namespace

// ---- install.legacyMsi --------------------------------------------------------------------------------------------

TEST(msi_codes_are_normalized) {
    std::wstring n;
    CHECK(install::NormalizeMsiCode(L"{12345678-90ab-cdef-1234-567890ABCDEF}", n) && n == L"{12345678-90AB-CDEF-1234-567890ABCDEF}");
    for (const wchar_t* bad : {L"", L"12345678-90ab-cdef-1234-567890abcdef", L"{12345678-90ab-cdef-1234-567890abcdeg}",
                               L"{12345678-90ab-cdef-1234567890abcdef0}", L"{12345678-90ab-cdef-1234-567890abcdef}x",
                               L"{12345678_90ab-cdef-1234-567890abcdef}", L"$(UpgradeCode)"}) {
        CHECK(!install::NormalizeMsiCode(bad, n));
    }
}

TEST(msi_results_map_to_setup_outcomes) {
    using install::MsiOutcome;
    CHECK(install::ClassifyMsiResult(ERROR_SUCCESS) == MsiOutcome::Removed);
    CHECK(install::ClassifyMsiResult(ERROR_UNKNOWN_PRODUCT) == MsiOutcome::NotInstalled);  // 1605: gone already, fine
    CHECK(install::ClassifyMsiResult(ERROR_SUCCESS_REBOOT_REQUIRED) == MsiOutcome::RebootRequired);  // 3010
    CHECK(install::ClassifyMsiResult(ERROR_SUCCESS_REBOOT_INITIATED) == MsiOutcome::RebootRequired);
    CHECK(install::ClassifyMsiResult(ERROR_INSTALL_ALREADY_RUNNING) == MsiOutcome::Busy);  // 1618
    CHECK(install::ClassifyMsiResult(ERROR_CANCELLED) == MsiOutcome::Declined);           // UAC prompt declined
    CHECK(install::ClassifyMsiResult(ERROR_INSTALL_USEREXIT) == MsiOutcome::Declined);
    CHECK(install::ClassifyMsiResult(ERROR_INSTALL_FAILURE) == MsiOutcome::Failed);  // 1603
    CHECK(install::ClassifyMsiResult(ERROR_ACCESS_DENIED) == MsiOutcome::Failed);
    // Per-machine and policy-assigned products need administrator rights; the user's own do not.
    CHECK(install::MsiNeedsElevation(install::MsiContext::Machine));
    CHECK(install::MsiNeedsElevation(install::MsiContext::UserManaged));
    CHECK(!install::MsiNeedsElevation(install::MsiContext::UserUnmanaged));
    CHECK(install::MsiexecRemoveArguments(L"{12345678-90AB-CDEF-1234-567890ABCDEF}") ==
          L"/x {12345678-90AB-CDEF-1234-567890ABCDEF} /qn REBOOT=ReallySuppress");
}

TEST(installer_busy_message_reaches_the_front_end) {
    // 1618 sets error.installerBusy on the outcome; it crosses the worker pipe, other keys do not.
    install::Outcome o;
    o.code = ErrorCode::AlreadyRunning;
    o.messageKey = "error.installerBusy";
    CHECK(install::OutcomeFromJson(install::OutcomeToJson(o)).messageKey == "error.installerBusy");
    for (const char* bad : {"installerBusy", "error.x/../y", "error.with space", "ui.title"}) {
        json::Value v = install::OutcomeToJson(o);
        v.set("messageKey", bad);
        CHECK(install::OutcomeFromJson(v).messageKey.empty());
    }
}

TEST(legacy_msi_codes_in_the_package_header) {
    const install::Extensions e = Ext(R"({"legacyMsi":{"productCodes":["{12345678-90ab-cdef-1234-567890abcdef}","{12345678-90AB-CDEF-1234-567890ABCDEF}"],
                                          "upgradeCodes":["{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"]}})");
    CHECK(e.legacyMsiProductCodes.size() == 1 && e.legacyMsiProductCodes[0] == L"{12345678-90AB-CDEF-1234-567890ABCDEF}");  // one, normalized
    CHECK(e.legacyMsiUpgradeCodes.size() == 1);
    CHECK(Ext("{}").legacyMsiProductCodes.empty() && Ext(R"({"legacyMsi":null})").legacyMsiUpgradeCodes.empty());
    CHECK_THROWS_CODE(Ext(R"({"legacyMsi":{"productCodes":["12345678-90ab-cdef-1234-567890abcdef"]}})"), ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Ext(R"({"legacyMsi":{"upgradeCodes":[42]}})"), ErrorCode::PackageCorrupt);
    std::string many = R"({"legacyMsi":{"upgradeCodes":[)";
    for (int i = 0; i < 17; ++i) many += (i ? "," : "") + std::string(R"("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")");
    CHECK_THROWS_CODE(Ext(many + "]}}"), ErrorCode::PackageCorrupt);
}

TEST(unknown_msi_codes_find_nothing) {
    // Read-only queries (MsiEnumRelatedProducts, MsiEnumProductsEx) for codes no product has.
    CHECK(install::FindMsiProducts({RandomGuid()}, {RandomGuid()}).empty());
    CHECK(install::FindMsiProducts({}, {}).empty());
    CHECK(install::FindMsiProducts({L"not a code"}, {L"{nope}"}).empty());
}

// ---- update channels ------------------------------------------------------------------------------------------------

TEST(update_channel_names) {
    for (const wchar_t* good : {L"stable", L"beta", L"insider-2", L"a"}) CHECK(install::IsUpdateChannelName(good));
    for (const wchar_t* bad : {L"", L"Beta", L"2beta", L"-beta", L"be ta", L"beta_1", L"beta.1", L"abcdefghijklmnopqrstuvwxyzabcdefg"}) {
        CHECK(!install::IsUpdateChannelName(bad));
    }
    // /channel, else the recorded one, else update.channel, else stable; names that are not channel names are skipped.
    CHECK(boot::EffectiveChannel(L"beta", L"dev", L"stable") == L"beta");
    CHECK(boot::EffectiveChannel(L"", L"dev", L"beta") == L"dev");
    CHECK(boot::EffectiveChannel(L"", L"", L"beta") == L"beta");
    CHECK(boot::EffectiveChannel(L"", L"", L"") == L"stable");
    CHECK(boot::EffectiveChannel(L"Bad Name", L"", L"beta") == L"beta");
}

TEST(feeds_without_channels_are_the_stable_release) {
    const json::Value feed = Json(Entry("1.2.0"));
    boot::FeedRelease r;
    std::wstring error;
    CHECK(boot::PickFeedRelease(feed, L"stable", r, error) == boot::FeedLookup::Found && r.version == L"1.2.0" && r.channel == L"stable");
    CHECK(r.url == "https://example.com/App-1.2.0.exe" && r.size == 10);
    // Any other channel gets the stable release too.
    CHECK(boot::PickFeedRelease(feed, L"beta", r, error) == boot::FeedLookup::Found && r.version == L"1.2.0" && r.channel == L"stable");
    // Malformed feeds are refused with a reason.
    CHECK(boot::PickFeedRelease(Json(R"({"version":"1.2.0","url":"http://example.com/x.exe","sha256":")" + kHash + R"("})"), L"stable", r, error) ==
              boot::FeedLookup::Invalid && !error.empty());
    CHECK(boot::PickFeedRelease(Json(R"({"url":"https://example.com/x.exe"})"), L"stable", r, error) == boot::FeedLookup::Invalid);
    CHECK(boot::PickFeedRelease(Json("[]"), L"stable", r, error) == boot::FeedLookup::Invalid);
}

TEST(feed_channels) {
    boot::FeedRelease r;
    std::wstring error;
    // A beta ahead of stable: beta users get it, everyone else the stable release.
    const json::Value ahead = Json(Entry("1.2.0").substr(0, Entry("1.2.0").size() - 1) + R"(,"channels":{"beta":)" + Entry("1.3.0-beta.1") + "}}");
    CHECK(boot::PickFeedRelease(ahead, L"stable", r, error) == boot::FeedLookup::Found && r.version == L"1.2.0");
    CHECK(boot::PickFeedRelease(ahead, L"beta", r, error) == boot::FeedLookup::Found && r.version == L"1.3.0-beta.1" && r.channel == L"beta");
    CHECK(boot::PickFeedRelease(ahead, L"dev", r, error) == boot::FeedLookup::Found && r.version == L"1.2.0");  // no such channel: stable
    // Stable caught up (1.3.0 > 1.3.0-beta.1): beta users get the stable release.
    const json::Value behind = Json(Entry("1.3.0").substr(0, Entry("1.3.0").size() - 1) + R"(,"channels":{"beta":)" + Entry("1.3.0-beta.1") + "}}");
    CHECK(boot::PickFeedRelease(behind, L"beta", r, error) == boot::FeedLookup::Found && r.version == L"1.3.0" && r.channel == L"stable");
    // Only a beta so far: nothing for stable, the beta for beta.
    const json::Value betaOnly = Json(R"({"channels":{"beta":)" + Entry("0.9.0-beta.2") + "}}");
    CHECK(boot::PickFeedRelease(betaOnly, L"stable", r, error) == boot::FeedLookup::None);
    CHECK(boot::PickFeedRelease(betaOnly, L"beta", r, error) == boot::FeedLookup::Found && r.version == L"0.9.0-beta.2");
    // channels.stable stands for the top-level fields when those are missing.
    const json::Value stableInChannels = Json(R"({"channels":{"stable":)" + Entry("2.0.0") + "}}");
    CHECK(boot::PickFeedRelease(stableInChannels, L"stable", r, error) == boot::FeedLookup::Found && r.version == L"2.0.0");
    // A malformed entry of the channel asked for, or "channels" that is not an object, is an error.
    CHECK(boot::PickFeedRelease(Json(Entry("1.2.0").substr(0, Entry("1.2.0").size() - 1) + R"(,"channels":{"beta":{"version":"x"}}})"), L"beta", r,
                                error) == boot::FeedLookup::Invalid);
    CHECK(boot::PickFeedRelease(Json(Entry("1.2.0").substr(0, Entry("1.2.0").size() - 1) + R"(,"channels":[]})"), L"stable", r, error) ==
          boot::FeedLookup::Invalid);
    CHECK(boot::PickFeedRelease(ahead, L"Not A Channel", r, error) == boot::FeedLookup::Invalid);
    // Repair: the installed version in any channel.
    CHECK(boot::FindFeedVersion(ahead, L"1.3.0-beta.1", r, error) == boot::FeedLookup::Found && r.channel == L"beta");
    CHECK(boot::FindFeedVersion(ahead, L"1.2.0", r, error) == boot::FeedLookup::Found && r.channel == L"stable");
    CHECK(boot::FindFeedVersion(ahead, L"1.1.0", r, error) == boot::FeedLookup::None && error.find(L"1.3.0-beta.1") != std::wstring::npos);
}

TEST(channel_switch_and_record) {
    boot::Args a;
    std::wstring err;
    CHECK(boot::ParseArgs(L"uninstall.exe /check-update /channel beta", a, err) && a.checkUpdate && a.channel == L"beta");
    boot::Args b;
    CHECK(boot::ParseArgs(L"uninstall.exe /install-update /quiet --channel=Beta", b, err) && b.channel == L"beta");  // lower-cased
    boot::Args c;
    CHECK(boot::ParseArgs(L"Setup.exe /quiet /channel insider", c, err) && c.channel == L"insider");
    for (const wchar_t* bad : {L"uninstall.exe /check-update /channel", L"uninstall.exe /check-update /channel \"be ta\"",
                               L"uninstall.exe /uninstall /channel beta", L"uninstall.exe /modify /channel beta"}) {
        boot::Args d;
        CHECK(!boot::ParseArgs(bad, d, err));
    }
    // The install record keeps the channel; an invalid one in a record is dropped.
    install::InstallRecord r;
    r.appId = L"T";
    r.installDir = L"C:\\Apps\\T";
    r.updateChannel = L"beta";
    CHECK(install::RecordFromJson(install::RecordToJson(r)).updateChannel == L"beta");
    json::Value bad = install::RecordToJson(r);
    bad.set("updateChannel", "Beta Channel");
    CHECK(install::RecordFromJson(bad).updateChannel.empty());
    // ... and travels to the elevated worker with the other options.
    install::InstallOptions o;
    o.installDir = L"C:\\Apps\\T";
    o.updateChannel = L"beta";
    install::InstallOptions back;
    CHECK(install::OptionsFromJson(install::OptionsToJson(o), back) && back.updateChannel == L"beta");
    json::Value tampered = install::OptionsToJson(o);
    tampered.set("updateChannel", "../x");
    CHECK(!install::OptionsFromJson(tampered, back));
}
