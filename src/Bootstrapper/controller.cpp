#include "Bootstrapper/controller.h"

#include "Bootstrapper/elevation.h"
#include "Bootstrapper/updater.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"
#include "IPC/protocol.h"

namespace fsetup::boot {

using install::Offer;
using install::Scope;

namespace {

constexpr int kRebootRequiredExitCode = 3010;  // ERROR_SUCCESS_REBOOT_REQUIRED, as msiexec and most installers return

const char* InputTypeName(install::InputDef::Type t) noexcept {
    switch (t) {
        case install::InputDef::Type::Password: return "password";
        case install::InputDef::Type::Choice: return "choice";
        default: return "text";
    }
}

bool IsLockableFile(const std::wstring& rel) {
    const size_t dot = rel.find_last_of(L'.');
    if (dot == std::wstring::npos) return false;
    const std::wstring ext = rel.substr(dot);
    return EqualsIgnoreCase(ext, L".exe") || EqualsIgnoreCase(ext, L".dll");
}

}  // namespace

bool IsRetryable(ErrorCode code) noexcept {
    switch (code) {
        case ErrorCode::PackageCorrupt:
        case ErrorCode::UnsupportedSystem:
        case ErrorCode::DowngradeBlocked:
        case ErrorCode::NotInstalled:
        case ErrorCode::InvalidArguments:
        case ErrorCode::AlreadyRunning: return false;
        default: return true;
    }
}

Controller::Controller(const install::Package& pkg, const archive::Container& container, Mode mode,
                       const Strings& strings, const Args& args, std::wstring logPath)
    : pkg_(pkg), container_(container), mode_(mode), strings_(strings), args_(args), logPath_(std::move(logPath)) {
    doneEvent_.reset(::CreateEventW(nullptr, TRUE, FALSE, nullptr));
    install::Engine engine(pkg_, container_);
    existing_ = engine.DetectExisting();
}

Controller::~Controller() {
    if (thread_.joinable()) thread_.join();
}

install::InstallOptions Controller::DefaultOptions() const {
    install::Engine engine(pkg_, container_);
    install::InstallOptions o;
    o.scope = pkg_.defaultScope;
    if (existing_.product) o.scope = existing_.product->scope;
    else if (args_.machineScope && engine.ScopeAllowed(*args_.machineScope ? Scope::Machine : Scope::User)) {
        o.scope = *args_.machineScope ? Scope::Machine : Scope::User;
    }
    o.installDir = engine.DefaultInstallDir(o.scope);
    if (!args_.installDir.empty() && !existing_.product && pkg_.allowCustomDir) o.installDir = args_.installDir;
    o.desktopShortcut = install::OfferDefault(pkg_.desktopShortcut);
    o.startMenuShortcut = install::OfferDefault(pkg_.startMenuShortcut);
    if (args_.desktopShortcut && install::OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = *args_.desktopShortcut;
    if (args_.startMenuShortcut && install::OfferVisible(pkg_.startMenuShortcut)) o.startMenuShortcut = *args_.startMenuShortcut;
    const bool unattended = args_.quiet || args_.passive;
    for (const auto& opt : pkg_.ext.options) {
        o.choices[opt.id] = opt.defaultValue;
        // e.g. "Enable now" that a scripted first install must not do (Inno Setup "skipifsilent").
        if (unattended && !existing_.product && opt.quietDefault && !opt.required) o.choices[opt.id] = *opt.quietDefault;
        if (auto it = args_.choices.find(opt.id); it != args_.choices.end()) o.choices[opt.id] = it->second;
    }
    // An update starts from the user's earlier choices: shortcuts and options from our install record, or the tasks an
    // adopted Inno Setup install recorded. Explicit switches (below) still win.
    const auto applyTasks = [&](const std::vector<std::wstring>& tasks, bool exclusive) {
        if (exclusive) {
            if (install::OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = false;
            for (const auto& opt : pkg_.ext.options) {
                if (!opt.required) o.choices[opt.id] = false;
            }
        }
        for (std::wstring t : tasks) {
            const bool on = t.empty() || t.front() != L'!';
            if (!on) t.erase(0, 1);
            if (t == L"desktopicon" && install::OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = on;
            for (const auto& opt : pkg_.ext.options) {
                if (!opt.required && EqualsIgnoreCase(Utf8ToWide(opt.id), t)) o.choices[opt.id] = on;
            }
        }
    };
    for (const auto& input : pkg_.ext.inputs) o.inputs[input.id] = input.defaultValue;
    if (existing_.product && !existing_.foreign) {
        try {
            const install::InstallRecord previous = install::LoadVerifiedRecord(*existing_.product);
            // Remembered input values (never passwords: the engine does not store them) pre-fill an update or a change.
            for (const auto& [id, value] : previous.inputs) {
                if (const install::InputDef* input = pkg_.ext.FindInput(id); input && input->remember) o.inputs[id] = value;
            }
            const auto under = [&](const std::wstring& link, std::initializer_list<KNOWNFOLDERID> ids) {
                for (const auto& id : ids) {
                    const std::wstring dir = path::KnownFolder(id);
                    if (!dir.empty() && path::IsSameOrUnder(link, dir)) return true;
                }
                return false;
            };
            if (install::OfferVisible(pkg_.desktopShortcut)) {
                o.desktopShortcut = std::any_of(previous.shortcuts.begin(), previous.shortcuts.end(),
                                                [&](const std::wstring& l) { return under(l, {FOLDERID_Desktop, FOLDERID_PublicDesktop}); });
            }
            if (install::OfferVisible(pkg_.startMenuShortcut)) {
                o.startMenuShortcut = std::any_of(previous.shortcuts.begin(), previous.shortcuts.end(),
                                                  [&](const std::wstring& l) { return under(l, {FOLDERID_Programs, FOLDERID_CommonPrograms}); });
            }
            for (const auto& [id, on] : previous.choices) {
                if (o.choices.count(id)) o.choices[id] = on;
            }
        } catch (const Failure&) {
            // No usable record: keep the package defaults.
        }
    } else if (existing_.product && !existing_.product->innoTasks.empty()) {
        std::vector<std::wstring> tasks;
        for (size_t start = 0; start <= existing_.product->innoTasks.size();) {
            size_t end = existing_.product->innoTasks.find(L',', start);
            if (end == std::wstring::npos) end = existing_.product->innoTasks.size();
            tasks.push_back(ToLower(existing_.product->innoTasks.substr(start, end - start)));
            start = end + 1;
        }
        applyTasks(tasks, true);
    }
    // What is on the PC wins over remembered choices: an app that turns its own autostart on and off, or a feature the
    // user switched off inside the app, is updated in the state it is in now.
    if (existing_.product) {
        for (const auto& opt : pkg_.ext.options) {
            if (opt.required || !opt.HasDetection()) continue;
            const std::wstring runValue = !opt.detectRunValue.empty() ? opt.detectRunValue : opt.startupName;
            const bool found = (!runValue.empty() && install::RunValueExists(runValue)) ||
                               (!opt.detectScheduledTask.empty() && install::ScheduledTaskExists(opt.detectScheduledTask));
            log::Info(L"Option %S detected as %s", opt.id.c_str(), found ? L"on" : L"off");
            o.choices[opt.id] = found;
        }
    }
    if (args_.tasks) applyTasks(*args_.tasks, !args_.tasksMerge);
    // Explicit switches win over everything above.
    if (args_.desktopShortcut && install::OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = *args_.desktopShortcut;
    if (args_.startMenuShortcut && install::OfferVisible(pkg_.startMenuShortcut)) o.startMenuShortcut = *args_.startMenuShortcut;
    for (const auto& [id, on] : args_.choices) {
        if (o.choices.count(id)) o.choices[id] = on;
    }
    for (const auto& [id, value] : args_.inputs) {
        if (pkg_.ext.FindInput(id)) o.inputs[id] = value;  // unknown ids are refused before a session starts
    }
    o.removeUserData = mode_ == Mode::Uninstall && (args_.removeUserData || (!unattended && pkg_.ext.userData.removeByDefault));
    // Variant: /variant, else the one already installed (an update keeps the user's earlier choice), else the default.
    o.variant = pkg_.DefaultVariant();
    if (existing_.product && pkg_.FindVariant(existing_.product->variant)) o.variant = existing_.product->variant;
    if (!args_.variant.empty() && pkg_.FindVariant(args_.variant)) o.variant = args_.variant;
    return o;
}

bool Controller::SanitizeOptions(const json::Value& v, install::InstallOptions& out) const {
    if (!install::OptionsFromJson(v, out)) return false;
    install::Engine engine(pkg_, container_);
    if (!engine.ScopeAllowed(out.scope)) return false;
    if (existing_.product && out.scope != existing_.product->scope) return false;
    const install::InstallOptions defaults = DefaultOptions();
    if (!pkg_.allowCustomDir || existing_.product) {
        if (!EqualsIgnoreCase(out.installDir, engine.DefaultInstallDir(out.scope))) return false;
    }
    if (!install::OfferVisible(pkg_.desktopShortcut)) out.desktopShortcut = defaults.desktopShortcut;
    if (!install::OfferVisible(pkg_.startMenuShortcut)) out.startMenuShortcut = defaults.startMenuShortcut;
    std::map<std::string, bool> choices = defaults.choices;
    for (const auto& [id, on] : out.choices) {
        if (!choices.count(id)) return false;  // only options the package defines
        choices[id] = on;
    }
    out.choices = std::move(choices);
    // Inputs: only ids the package defines (OptionsFromJson merged the front end's values over the defaults). Whether
    // the values are acceptable is checked when the operation starts (InputProblem), with a precise message.
    for (const auto& [id, value] : out.inputs) {
        if (!pkg_.ext.FindInput(id)) return false;
    }
    return engine.SanitizeOptions(out);  // the same package rules the elevated worker enforces
}

std::wstring Controller::InputProblem(const install::InstallOptions& o) const {
    for (const auto& [id, value] : o.inputs) {
        if (!pkg_.ext.FindInput(id)) return L"Unknown input: " + Utf8ToWide(id);
    }
    for (const auto& input : pkg_.ext.inputs) {
        const auto it = o.inputs.find(input.id);
        const std::wstring reason = install::CheckInputValue(input, it == o.inputs.end() ? std::wstring() : it->second);
        if (!reason.empty()) return L"Input '" + Utf8ToWide(input.id) + L"': " + reason;
    }
    return {};
}

json::Value Controller::CheckInput(const std::string& id, const std::wstring& value) const {
    json::Value r;
    r.set("t", "inputCheck");
    r.set("id", id);
    r.set("value", value);  // the front end ignores replies for a value it no longer shows
    const install::InputDef* input = pkg_.ext.FindInput(id);
    const std::wstring reason = input ? install::CheckInputValue(*input, value) : std::wstring(L"unknown input");
    r.set("ok", reason.empty());
    r.set("message", reason.empty() ? std::wstring() : strings_.Get(input && input->required && value.empty() ? "inputs.required" : "inputs.invalid"));
    return r;
}

std::optional<install::InstallRecord> Controller::PreviousRecord() const {
    if (!existing_.product || existing_.foreign) return std::nullopt;
    try {
        return install::LoadVerifiedRecord(*existing_.product);
    } catch (const Failure&) {
        return std::nullopt;
    }
}

bool Controller::ReadFrontendFile(const std::wstring& relativePath, std::vector<uint8_t>& out, uint64_t maxBytes) const {
    out.clear();
    const install::FileSet& set = pkg_.frontend;
    const install::FileInfo* file = nullptr;
    for (const auto& f : set.files) {
        if (EqualsIgnoreCase(f.path, relativePath)) file = &f;
    }
    if (!file || file->size > maxBytes || file->stream >= set.streams.size()) return false;
    try {
        const install::StreamInfo& si = set.streams[file->stream];
        archive::StreamReader reader(container_, si.offset, si.size, si.rawSize, si.sha256);
        std::vector<uint8_t> skip(64 * 1024);
        for (uint64_t at = 0; at < file->offset;) {  // files before it in the same solid stream
            const size_t n = reader.Read(skip.data(), static_cast<size_t>(std::min<uint64_t>(skip.size(), file->offset - at)));
            if (n == 0) return false;
            at += n;
        }
        out.resize(static_cast<size_t>(file->size));
        for (size_t got = 0; got < out.size();) {
            const size_t n = reader.Read(out.data() + got, out.size() - got);
            if (n == 0) return out.clear(), false;
            got += n;
        }
        if (Sha256::Of(out) != file->sha256) return out.clear(), false;
    } catch (const Failure& f) {
        log::Warn(L"Cannot read %s from the package: %s", relativePath.c_str(), f.detail().c_str());
        out.clear();
        return false;
    }
    return true;
}

json::Value Controller::Hello(const std::wstring& iconPath, const std::wstring& bannerPath) const {
    install::Engine engine(pkg_, container_);
    const install::InstallOptions defaults = DefaultOptions();
    json::Value h;
    h.set("t", "hello");
    h.set("proto", ipc::kProtocolVersion);
    h.set("mode", mode_ == Mode::Uninstall ? "uninstall" : mode_ == Mode::Modify ? "modify" : "install");
    h.set("elevated", security::IsElevated());
    // Progress only: the front end starts right away with these defaults and closes itself after a success.
    h.set("passive", args_.passive && !args_.quiet);
    h.set("strings", strings_.Table());
    h.set("language", strings_.Language());

    json::Value app;
    app.set("name", pkg_.name);
    app.set("publisher", pkg_.publisher);
    app.set("version", mode_ != Mode::Install && existing_.product ? existing_.product->version : pkg_.version);
    app.set("iconPath", iconPath);
    h.set("app", app);

    // Who signed the running file: "Verified publisher: X" or "Unknown publisher" on the first page.
    std::call_once(signerOnce_, [this] { signer_ = archive::GetSignerInfo(path::ModulePath()); });
    json::Value signature;
    signature.set("state", signer_.state == archive::SignatureState::Valid      ? "valid"
                           : signer_.state == archive::SignatureState::Unsigned ? "unsigned"
                                                                                : "untrusted");
    signature.set("publisher", signer_.state == archive::SignatureState::Valid ? signer_.publisher : std::wstring());
    h.set("signature", signature);

    json::Value ui;
    const json::Value& u = pkg_.ui;
    ui.set("theme", u["theme"].isString() ? u["theme"] : json::Value("system"));
    ui.set("backdrop", u["backdrop"].isString() ? u["backdrop"] : json::Value("mica"));
    ui.set("useSystemAccent", u["useSystemAccent"].asBool(true));
    ui.set("accentColor", u["accentColor"].asString());
    ui.set("title", u["title"].asString());
    ui.set("welcomeText", u["welcomeText"].asString());
    ui.set("finishText", u["finishText"].asString());
    ui.set("bannerPath", bannerPath);
    // The license is shown before a first install, or before every install when the package says so (e.g. an agreement
    // that changes between versions).
    if (mode_ == Mode::Install && (!existing_.product || u["licenseMode"].asString() == "always")) {
        const json::Value& localized = u["licenses"][WideToUtf8(strings_.Language())];
        ui.set("licenseText", localized.isString() && !localized.asString().empty() ? localized.asString() : u["license"].asString());
    }
    h.set("ui", ui);

    json::Value opts;
    std::wstring dir = defaults.installDir;
    if (mode_ == Mode::Uninstall && existing_.product) dir = existing_.product->installDir;
    opts.set("installDir", dir);
    opts.set("scope", install::ScopeName(defaults.scope));
    opts.set("canChangeDir", pkg_.allowCustomDir && !existing_.product);
    opts.set("canChangeScope", pkg_.scopeMode == install::ScopeMode::UserOrMachine && !existing_.product);
    opts.set("userDir", engine.DefaultInstallDir(Scope::User));
    opts.set("machineDir", engine.DefaultInstallDir(Scope::Machine));
    opts.set("dirLeaf", path::SanitizeName(pkg_.name));
    const auto offer = [](Offer o, bool value) {
        json::Value v;
        v.set("visible", install::OfferVisible(o));
        v.set("value", value);
        return v;
    };
    opts.set("desktopShortcut", offer(pkg_.desktopShortcut, defaults.desktopShortcut));
    opts.set("startMenuShortcut", offer(pkg_.startMenuShortcut, defaults.startMenuShortcut));
    opts.set("launchAfter", offer(pkg_.launchAfterInstall, install::OfferDefault(pkg_.launchAfterInstall)));
    json::Value choices = json::Value::MakeArray();
    if (mode_ != Mode::Uninstall) {
        for (const auto& opt : pkg_.ext.options) {
            json::Value c;
            c.set("id", opt.id);
            c.set("label", opt.label.Get(strings_.Language(), Utf8ToWide(opt.id)));
            c.set("value", opt.required || (defaults.choices.count(opt.id) ? defaults.choices.at(opt.id) : opt.defaultValue));
            c.set("required", opt.required);  // shown checked and locked
            choices.push(c);
        }
    }
    opts.set("choices", choices);
    if (mode_ == Mode::Install) {
        // Report a problem with the default location up front so the welcome page can route to the options.
        const json::Value check = CheckPath(dir, defaults.scope);
        opts.set("dirOk", check["ok"].asBool());
        opts.set("dirMessage", check["message"]);
    } else {
        opts.set("dirOk", true);
    }
    h.set("options", opts);

    // Inputs (the "Settings" page): current values are /input, else the remembered ones, else the defaults.
    json::Value inputs = json::Value::MakeArray();
    bool inputsPage = false;
    if (mode_ != Mode::Uninstall) {
        const std::wstring& language = strings_.Language();
        for (const auto& input : pkg_.ext.inputs) {
            json::Value i;
            i.set("id", input.id);
            i.set("label", input.label.Get(language, Utf8ToWide(input.id)));
            i.set("description", input.description.Get(language, L""));
            i.set("type", InputTypeName(input.type));
            const auto it = defaults.inputs.find(input.id);
            const std::wstring value = it == defaults.inputs.end() ? input.defaultValue : it->second;
            i.set("value", value);
            i.set("required", input.required);
            i.set("pattern", input.pattern);
            json::Value list = json::Value::MakeArray();
            for (const auto& c : input.choices) {
                json::Value item;
                item.set("value", c.value);
                item.set("label", c.label.Get(language, c.value));
                list.push(item);
            }
            i.set("choices", list);
            const json::Value check = CheckInput(input.id, value);
            i.set("ok", check["ok"]);
            i.set("message", check["message"]);
            inputs.push(i);
            if (!check["ok"].asBool()) inputsPage = true;  // a required value is missing, or one is no longer accepted
        }
        // A first install asks; an update asks only for what it cannot fill in; a change always offers the page.
        if (!pkg_.ext.inputs.empty() && (mode_ == Mode::Modify || !existing_.product)) inputsPage = true;
    }
    h.set("inputs", inputs);
    h.set("inputsPage", inputsPage);

    json::Value modify;
    if (mode_ == Mode::Modify) {
        modify.set("canRepair", existing_.product.has_value() && !existing_.foreign);
        modify.set("startRepair", args_.repair);  // interactive /repair: the front end starts the repair at once
    }
    h.set("modify", modify);

    json::Value ex;
    const char* relation = "none";
    switch (existing_.relation) {
        case install::Relation::Upgrade: relation = "upgrade"; break;
        case install::Relation::Same: relation = "same"; break;
        case install::Relation::Downgrade: relation = "downgrade"; break;
        default: break;
    }
    ex.set("relation", mode_ == Mode::Uninstall ? "none" : relation);
    ex.set("version", existing_.product ? existing_.product->version : std::wstring());
    ex.set("blocked", mode_ == Mode::Install && existing_.blocked);
    h.set("existing", ex);
    // Running copies of the app will be closed when the operation starts; tell the user up front.
    std::wstring running;
    for (const auto& n : install::RunningProcesses(pkg_.ext.closeProcesses)) running += (running.empty() ? L"" : L", ") + n;
    h.set("runningApps", running);
    // Other programs that use the installation's programs and libraries (Restart Manager closes them when the operation
    // starts, and restarts those that registered for it afterwards).
    std::wstring locking;
    if (pkg_.ext.restartManager) {
        if (const auto record = PreviousRecord()) {
            std::vector<std::wstring> files;
            for (const auto& f : record->files) {
                if (files.size() < 4000 && IsLockableFile(f.path)) files.push_back(path::Join(record->installDir, f.path));
            }
            for (const auto& n : install::LockingApps(files)) locking += (locking.empty() ? L"" : L", ") + n;
        }
    }
    h.set("lockingApps", locking);
    // Prerequisites that are missing on this PC and what setup will do about them (per variant: a self-contained build
    // needs no shared runtime).
    const auto prerequisiteNotice = [&](const std::string& variant) {
        std::wstring notice;
        if (mode_ != Mode::Install) return notice;
        for (const auto& req : pkg_.ext.requirements) {
            if (!req.AppliesTo(variant) || install::RequirementSatisfied(req)) continue;
            std::wstring line;
            const std::wstring found = install::InstalledRequirementVersion(req);
            if (!found.empty() && req.minVersion) {
                // Present but older than the package needs.
                line = strings_.Fmt(req.Installable() ? "prereq.upgrade" : "prereq.tooOld", {req.name, found, req.minVersionText});
                if (!req.Installable() && !req.helpUrl.empty()) line += L" " + Utf8ToWide(req.helpUrl);
            } else if (!req.installer.empty()) line = strings_.Fmt("prereq.bundled", {req.name});
            else if (!req.downloadUrl.empty()) line = strings_.Fmt("prereq.download", {req.name, req.downloadSize ? FormatBytes(req.downloadSize) : std::wstring(L"?")});
            else line = strings_.Fmt("prereq.missing", {req.name}) + (req.helpUrl.empty() ? L"" : L" " + Utf8ToWide(req.helpUrl));
            notice += (notice.empty() ? L"" : L"\n") + line;
        }
        return notice;
    };
    h.set("prerequisites", prerequisiteNotice(defaults.variant));
    json::Value variants = json::Value::MakeArray();
    if (mode_ == Mode::Install) {
        for (const auto& v : pkg_.variants) {
            json::Value item;
            item.set("id", v.id);
            item.set("label", v.label.Get(strings_.Language(), Utf8ToWide(v.id)));
            item.set("description", v.description.Get(strings_.Language(), L""));
            item.set("requiredBytes", engine.RequiredBytes(v.id));
            item.set("prerequisites", prerequisiteNotice(v.id));
            variants.push(item);
        }
    }
    h.set("variants", variants);
    // Uninstall: offer to delete the app's settings and data (kept by default unless the package says otherwise).
    json::Value userData;
    if (mode_ == Mode::Uninstall) {
        std::wstring where;
        for (const auto& tpl : pkg_.ext.userData.paths) {
            const std::wstring p = install::ExpandDataTemplate(tpl, existing_.product ? existing_.product->installDir : std::wstring());
            if (!p.empty() && fs::Exists(p)) where += (where.empty() ? L"" : L"\n") + p;
        }
        userData.set("visible", !where.empty());
        userData.set("value", defaults.removeUserData);
        userData.set("paths", where);
    }
    h.set("userData", userData);
    h.set("variant", defaults.variant);
    h.set("requiredBytes", engine.RequiredBytes(defaults.variant));
    h.set("freeBytes", fs::FreeSpace(dir));
    return h;
}

json::Value Controller::CheckPath(const std::wstring& dir, Scope scope) const {
    install::Engine engine(pkg_, container_);
    json::Value r;
    r.set("t", "pathCheck");
    r.set("path", dir);
    if (!engine.ScopeAllowed(scope)) scope = pkg_.defaultScope;
    // Per-machine targets are written by the elevated worker, so only probe writability for per-user installs.
    const install::DirCheck check = engine.CheckInstallDir(dir, scope, scope == Scope::User);
    r.set("ok", check.problem == path::Problem::None);
    r.set("normalized", check.normalized);
    r.set("requiredBytes", check.requiredBytes);
    r.set("freeBytes", check.freeBytes);
    std::wstring message;
    if (check.problem == path::Problem::Space) {
        message = strings_.Fmt("path.space", {FormatBytes(check.requiredBytes), FormatBytes(check.freeBytes)});
    } else if (check.problem != path::Problem::None) {
        message = strings_.Get(path::ProblemKey(check.problem));
    }
    r.set("message", message);
    return r;
}

bool Controller::Begin(Operation op) {
    {
        std::lock_guard lock(mutex_);
        if (running_) return false;
        running_ = true;
        started_ = true;
        outcome_.reset();
        operation_ = op;
        repair_ = RepairResult::None;
        cancel_ = false;
        ::ResetEvent(doneEvent_.get());
    }
    if (thread_.joinable()) thread_.join();
    return true;
}

void Controller::Start(const install::InstallOptions& options, HWND owner) {
    const Operation op = mode_ == Mode::Uninstall ? Operation::Uninstall : mode_ == Mode::Modify ? Operation::Modify : Operation::Install;
    if (!Begin(op)) return;
    thread_ = std::thread([this, options, owner] { Run(options, owner); });
}

void Controller::Repair(HWND /*owner*/) {
    if (mode_ != Mode::Modify || !Begin(Operation::Repair)) return;
    thread_ = std::thread([this] { RunRepair(); });
}

Operation Controller::operation() const {
    std::lock_guard lock(mutex_);
    return operation_;
}

void Controller::Cancel() {
    cancel_ = true;
    std::lock_guard lock(mutex_);
    if (workerChannel_) {
        json::Value m;
        m.set("t", "cancel");
        workerChannel_->Send(m);
    }
}

bool Controller::Started() const {
    std::lock_guard lock(mutex_);
    return started_;
}

bool Controller::Running() const {
    std::lock_guard lock(mutex_);
    return running_;
}

bool Controller::WaitDone(DWORD ms) const { return ::WaitForSingleObject(doneEvent_.get(), ms) == WAIT_OBJECT_0; }

std::optional<install::Outcome> Controller::Result() const {
    std::lock_guard lock(mutex_);
    return outcome_;
}

void Controller::Attach(IListener* listener, install::Progress* last, std::optional<install::Outcome>* outcome, bool* started) {
    std::lock_guard listenerLock(listenerMutex_);
    std::lock_guard lock(mutex_);
    listener_ = listener;
    if (last) *last = last_;
    if (outcome) *outcome = outcome_;
    if (started) *started = started_;
}

void Controller::Publish(const install::Progress& p) {
    {
        std::lock_guard lock(mutex_);
        last_ = p;
    }
    // Listeners are called without the state lock so they may query the controller.
    std::lock_guard listenerLock(listenerMutex_);
    if (listener_) listener_->OnProgress(p);
}

void Controller::Finish(install::Outcome o) {
    if (o.code == ErrorCode::Ok) log::Info(L"Result: Ok%s", o.rebootRequired ? L" (a restart is needed to finish)" : L"");
    else log::Warn(L"Result: %s%s%s", ErrorCodeName(o.code), o.detail.empty() ? L"" : L": ", o.detail.c_str());
    {
        std::lock_guard lock(mutex_);
        outcome_ = o;
        running_ = false;
        workerChannel_ = nullptr;
    }
    {
        std::lock_guard listenerLock(listenerMutex_);
        if (listener_) listener_->OnDone(o);
    }
    ::SetEvent(doneEvent_.get());
}

void Controller::Run(install::InstallOptions options, HWND owner) {
    struct Sink : install::IProgressSink {
        Controller* self;
        void OnProgress(const install::Progress& p) override { self->Publish(p); }
    } sink;
    sink.self = this;

    if (mode_ != Mode::Uninstall) {
        // Checked here for every front end and for /quiet: a value the package does not accept never reaches the engine.
        for (const auto& [id, value] : options.inputs) {
            const install::InputDef* input = pkg_.ext.FindInput(id);
            if (input && input->type == install::InputDef::Type::Password) log::Info(L"Input %S: (%zu characters)", id.c_str(), value.size());
            else log::Info(L"Input %S = \"%s\"", id.c_str(), value.c_str());
        }
        if (const std::wstring problem = InputProblem(options); !problem.empty()) {
            log::Error(L"%s", problem.c_str());
            install::Outcome rejected;
            rejected.code = ErrorCode::InvalidArguments;
            rejected.detail = problem;
            rejected.rolledBack = true;
            rejected.upgrade = existing_.product.has_value();
            Finish(std::move(rejected));
            return;
        }
    }
    const bool machine = mode_ == Mode::Install ? options.scope == Scope::Machine
                                                : (existing_.product && existing_.product->scope == Scope::Machine);
    // Changes the app made with administrator rights are undone after a per-user uninstall through one UAC prompt
    // (decided now: the markers may be part of the user data the uninstall removes).
    const bool elevatedCleanup = mode_ == Mode::Uninstall && !machine && !security::IsElevated() &&
                                 install::Engine(pkg_, container_).ElevatedCleanupApplies();
    install::Outcome outcome;
    if (machine && !security::IsElevated()) {
        outcome = RunElevated(options, owner);
    } else {
        install::Engine engine(pkg_, container_);
        if (const DWORD fault = ::GetEnvironmentVariableW(L"FLUENTSETUP_TEST_FAULT_AFTER", nullptr, 0); fault > 0) {
            // Test hook (e2e rollback test): only packages built with "fluentsetup build --test-hooks" honour it.
            if (pkg_.testHooks) {
                wchar_t buffer[16] = {};
                ::GetEnvironmentVariableW(L"FLUENTSETUP_TEST_FAULT_AFTER", buffer, 16);
                engine.SetFaultAfterFiles(static_cast<unsigned>(_wtoi(buffer)));
            } else {
                log::Warn(L"FLUENTSETUP_TEST_FAULT_AFTER is ignored: the package was not built with test hooks");
            }
        }
        switch (mode_) {
            case Mode::Install: outcome = engine.Install(options, sink, cancel_); break;
            case Mode::Modify: outcome = engine.Modify(options, sink, cancel_); break;
            case Mode::Uninstall: outcome = engine.Uninstall(args_.originDir, sink, cancel_, options.removeUserData); break;
        }
    }
    if (elevatedCleanup && outcome.code == ErrorCode::Ok) {
        if (args_.quiet) {
            log::Warn(L"Skipping the administrator cleanup in silent mode (it needs a UAC prompt)");
        } else {
            const install::Outcome cleanup = RunElevated(options, owner, "cleanup");
            outcome.warnings.insert(outcome.warnings.end(), cleanup.warnings.begin(), cleanup.warnings.end());
            if (cleanup.code != ErrorCode::Ok) outcome.warnings.push_back(strings_.Get("uninstall.adminCleanupSkipped"));
        }
    }
    Finish(std::move(outcome));
}

void Controller::RunRepair() {
    install::Outcome out;
    out.upgrade = true;  // a failure leaves the installation as it was
    out.installDir = existing_.product ? existing_.product->installDir : std::wstring();
    const auto finish = [&](RepairResult result) {
        {
            std::lock_guard lock(mutex_);
            repair_ = result;
        }
        Finish(std::move(out));
    };
    install::Progress checking;
    checking.state = install::State::Validating;
    Publish(checking);
    if (!existing_.product || existing_.foreign) {
        out.code = ErrorCode::NotInstalled;
        out.detail = L"The installation has no install record to verify";
        out.rolledBack = true;
        return finish(RepairResult::None);
    }
    const std::vector<std::wstring> damaged = install::Engine(pkg_, container_).VerifyFiles();
    if (damaged.empty()) {
        log::Info(L"Repair: every installed file is intact");
        return finish(RepairResult::NoProblems);
    }
    log::Warn(L"Repair: %zu file(s) missing or damaged", damaged.size());
    for (size_t i = 0; i < damaged.size() && i < 20; ++i) log::Info(L"  %s", damaged[i].c_str());
    if (cancel_) {
        out.code = ErrorCode::Cancelled;
        out.rolledBack = true;
        return finish(RepairResult::None);
    }

    // The same version's setup: the recorded original, else the update source.
    const auto record = PreviousRecord();
    RepairPackage source;
    std::wstring error;
    const auto progress = [this](uint64_t done, uint64_t total) {
        install::Progress p;
        p.state = install::State::Downloading;
        p.fraction = total ? static_cast<double>(done) / static_cast<double>(total) : -1;
        Publish(p);
    };
    const RepairLookup found = FindRepairPackage(pkg_, existing_.product->version, record ? &*record : nullptr, cancel_, progress, source, error);
    if (found != RepairLookup::Found) {
        out.rolledBack = true;
        if (cancel_) {
            out.code = ErrorCode::Cancelled;
            return finish(RepairResult::None);
        }
        out.code = found == RepairLookup::None ? ErrorCode::Failed : ErrorCode::UpdateFailed;
        out.detail = error.empty() ? L"No copy of this version's setup is available" : error;
        log::Error(L"Repair: %s", out.detail.c_str());
        return finish(found == RepairLookup::None ? RepairResult::NoSource : RepairResult::None);
    }

    install::Progress installing;
    installing.state = install::State::Installing;
    installing.cancellable = false;
    Publish(installing);
    // The setup takes the per-app lock itself; it reinstalls the same version with the recorded choices.
    if (releaseLock_) releaseLock_();
    DWORD exitCode = 0;
    const bool started = RunSetup(source.path, args_.quiet, strings_.Language(), exitCode, error);
    if (reacquireLock_ && !reacquireLock_()) log::Warn(L"Repair: another setup of %s took the single-instance lock", pkg_.appId.c_str());
    if (!started) {
        out.code = ErrorCode::Internal;
        out.detail = error;
        out.rolledBack = true;
        return finish(RepairResult::None);
    }
    log::Info(L"Repair: the setup exited with %lu", exitCode);
    if (exitCode == kRebootRequiredExitCode) {
        out.rebootRequired = true;
    } else if (exitCode != 0) {
        out.code = exitCode <= static_cast<DWORD>(ErrorCode::UpdateFailed) ? static_cast<ErrorCode>(exitCode) : ErrorCode::Failed;
        out.detail = Printf(L"The setup used for the repair (%s) ended with exit code %lu", path::FileName(source.path).c_str(), exitCode);
        out.rolledBack = true;
        return finish(RepairResult::None);
    }
    if (const auto still = install::Engine(pkg_, container_).VerifyFiles(); !still.empty()) {
        out.warnings.push_back(Printf(L"%zu file(s) still differ from the install record, e.g. %s", still.size(), still.front().c_str()));
    }
    finish(RepairResult::Repaired);
}

install::Outcome Controller::RunElevated(const install::InstallOptions& options, HWND owner, const char* op) {
    install::Outcome out;
    out.upgrade = existing_.product.has_value();
    install::Progress waiting;
    waiting.state = install::State::WaitingForElevation;
    waiting.cancellable = false;
    Publish(waiting);

    const std::wstring pipeName = L"FluentSetup-" + RandomToken();
    ipc::Channel server = ipc::Channel::CreateServer(pipeName);
    if (!server.valid()) {
        out.code = ErrorCode::Internal;
        out.detail = L"Cannot create the worker pipe";
        out.rolledBack = true;
        return out;
    }
    std::wstring params = L"--worker --pipe " + pipeName + L" --parent-pid " + std::to_wstring(::GetCurrentProcessId());
    if (mode_ == Mode::Uninstall) params += L" /uninstall";
    else if (mode_ == Mode::Modify) params += L" /modify";
    if (!args_.originDir.empty()) params += L" --origin-dir " + QuoteArg(args_.originDir);
    if (args_.phase2 && args_.originProcess) {
        params += L" --wait-pid " + std::to_wstring(::GetProcessId(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(args_.originProcess))));
    }
    if (!logPath_.empty()) params += L" /log " + QuoteArg(logPath_);
    params += L" /lang " + strings_.Language();

    log::Info(L"Requesting elevation for the worker");
    DWORD error = 0;
    UniqueHandle process = LaunchElevated(path::ModulePath(), params, owner, error);
    if (!process) {
        out.code = ErrorCode::ElevationRequired;
        out.detail = error == ERROR_CANCELLED ? L"The administrator permission request was declined"
                                              : L"Could not start the elevated worker: " + Win32Message(error);
        out.rolledBack = true;
        log::Warn(L"%s", out.detail.c_str());
        return out;
    }
    if (!server.Accept(60000, process.get()) || server.PeerProcessId() != ::GetProcessId(process.get())) {
        out.code = ErrorCode::Internal;
        out.detail = L"The elevated worker did not connect";
        ::TerminateProcess(process.get(), static_cast<UINT>(ErrorCode::Internal));
        out.rolledBack = true;
        return out;
    }
    json::Value run;
    run.set("t", "run");
    run.set("op", op ? op : mode_ == Mode::Uninstall ? "uninstall" : mode_ == Mode::Modify ? "modify" : "install");
    run.set("options", install::OptionsToJson(options));
    server.Send(run);
    {
        std::lock_guard lock(mutex_);
        workerChannel_ = &server;
    }
    if (cancel_) Cancel();

    bool done = false;
    json::Value message;
    while (server.Receive(message)) {
        const std::string& t = message["t"].asString();
        if (t == "progress") {
            Publish(install::ProgressFromJson(message["progress"]));
        } else if (t == "done") {
            out = install::OutcomeFromJson(message["outcome"]);
            done = true;
            break;
        }
    }
    {
        std::lock_guard lock(mutex_);
        workerChannel_ = nullptr;
    }
    if (!done) {
        ::WaitForSingleObject(process.get(), 5000);
        DWORD exitCode = 0;
        ::GetExitCodeProcess(process.get(), &exitCode);
        out.code = ErrorCode::Internal;
        out.detail = Printf(L"The elevated worker stopped unexpectedly (exit code 0x%08lX). Its journal will be rolled back "
                            L"the next time setup runs.", exitCode);
    }
    return out;
}

json::Value Controller::DoneMessage(const install::Outcome& o) const {
    json::Value m;
    m.set("t", "done");
    m.set("result", o.code == ErrorCode::Ok ? "success" : o.code == ErrorCode::Cancelled ? "cancelled" : "failed");
    m.set("code", static_cast<int64_t>(o.code));
    m.set("codeName", ErrorCodeName(o.code));
    m.set("message", strings_.Fmt("error." + WideToUtf8(ErrorCodeName(o.code)), {pkg_.name, o.detail}));
    json::Value warnings = json::Value::MakeArray();
    for (const auto& w : o.warnings) warnings.push(w);
    m.set("warnings", warnings);
    m.set("detail", o.detail);
    m.set("log", logPath_);
    m.set("installDir", o.installDir);
    m.set("canLaunch", o.code == ErrorCode::Ok && CanLaunch());
    m.set("launchDefault", install::OfferDefault(pkg_.launchAfterInstall));
    m.set("restored", o.rolledBack && o.upgrade);
    m.set("noChanges", o.rolledBack && !o.upgrade);
    m.set("leftovers", o.leftovers);
    Operation op;
    RepairResult repair;
    {
        std::lock_guard lock(mutex_);
        op = operation_;
        repair = repair_;
    }
    m.set("operation", op == Operation::Repair ? "repair" : op == Operation::Modify ? "modify" : op == Operation::Uninstall ? "uninstall" : "install");
    m.set("repair", repair == RepairResult::NoProblems ? "noProblems" : repair == RepairResult::Repaired ? "repaired"
                    : repair == RepairResult::NoSource ? "noSource" : "");
    if (repair == RepairResult::NoSource) m.set("message", strings_.Fmt("modify.repairNoSource", {pkg_.name}));
    m.set("rebootRequired", o.code == ErrorCode::Ok && o.rebootRequired);
    m.set("retryable", IsRetryable(o.code) && repair != RepairResult::NoSource);
    return m;
}

bool Controller::CanLaunch() const {
    if (mode_ != Mode::Install || pkg_.launchAfterInstall == Offer::Off) return false;
    const auto result = Result();
    return result && result->code == ErrorCode::Ok && !result->mainExecutable.empty();
}

void Controller::LaunchApp(const std::vector<std::wstring>& args) const {
    const auto result = Result();
    if (!result || result->mainExecutable.empty()) return;
    const std::wstring& exe = result->mainExecutable;
    if (!path::IsStrictlyUnder(exe, result->installDir) || !fs::Exists(exe)) return;
    log::Info(L"Launching %s", exe.c_str());
    std::wstring params;
    for (const auto& a : args) params += (params.empty() ? L"" : L" ") + QuoteArg(a);
    ShellOpenAsUser(exe, result->installDir, params);
}

int Controller::ExitCode() const {
    const auto result = Result();
    if (result && result->code == ErrorCode::Ok && result->rebootRequired) return kRebootRequiredExitCode;
    if (result) return static_cast<int>(result->code);
    return static_cast<int>(ErrorCode::Cancelled);  // closed before anything happened
}

void Controller::CompleteDeferred(HANDLE originProcess) const {
    const auto result = Result();
    if (!result || result->deferredDeletes.empty()) return;
    if (originProcess) ::WaitForSingleObject(originProcess, 60000);
    for (const auto& p : result->deferredDeletes) {
        for (int attempt = 0; attempt < 20; ++attempt) {
            const DWORD attrs = fs::Attributes(p);
            if (attrs == INVALID_FILE_ATTRIBUTES) break;
            if (attrs & FILE_ATTRIBUTE_DIRECTORY) {
                if (!fs::IsDirectoryEmpty(p)) break;
                if (::RemoveDirectoryW(path::Long(p).c_str())) break;
            } else if (fs::DeleteFileForce(p) == 0) {
                break;
            }
            ::Sleep(250);
        }
    }
}

}  // namespace fsetup::boot
