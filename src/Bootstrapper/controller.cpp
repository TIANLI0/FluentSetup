#include "Bootstrapper/controller.h"

#include "Bootstrapper/elevation.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"
#include "IPC/protocol.h"

namespace fsetup::boot {

using install::Offer;
using install::Scope;

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
    for (const auto& opt : pkg_.ext.options) {
        o.choices[opt.id] = opt.defaultValue;
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
    if (existing_.product && !existing_.foreign) {
        try {
            const install::InstallRecord previous = install::LoadVerifiedRecord(*existing_.product);
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
    if (args_.tasks) applyTasks(*args_.tasks, !args_.tasksMerge);
    // Explicit switches win over everything above.
    if (args_.desktopShortcut && install::OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = *args_.desktopShortcut;
    if (args_.startMenuShortcut && install::OfferVisible(pkg_.startMenuShortcut)) o.startMenuShortcut = *args_.startMenuShortcut;
    for (const auto& [id, on] : args_.choices) {
        if (o.choices.count(id)) o.choices[id] = on;
    }
    o.removeUserData = mode_ == Mode::Uninstall && (args_.removeUserData || (!args_.quiet && pkg_.ext.userData.removeByDefault));
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
    return engine.SanitizeOptions(out);  // the same package rules the elevated worker enforces
}

json::Value Controller::Hello(const std::wstring& iconPath) const {
    install::Engine engine(pkg_, container_);
    const install::InstallOptions defaults = DefaultOptions();
    json::Value h;
    h.set("t", "hello");
    h.set("proto", ipc::kProtocolVersion);
    h.set("mode", mode_ == Mode::Uninstall ? "uninstall" : "install");
    h.set("elevated", security::IsElevated());
    h.set("strings", strings_.Table());
    h.set("language", strings_.Language());

    json::Value app;
    app.set("name", pkg_.name);
    app.set("publisher", pkg_.publisher);
    app.set("version", mode_ == Mode::Uninstall && existing_.product ? existing_.product->version : pkg_.version);
    app.set("iconPath", iconPath);
    h.set("app", app);

    json::Value ui;
    const json::Value& u = pkg_.ui;
    ui.set("theme", u["theme"].isString() ? u["theme"] : json::Value("system"));
    ui.set("backdrop", u["backdrop"].isString() ? u["backdrop"] : json::Value("mica"));
    ui.set("useSystemAccent", u["useSystemAccent"].asBool(true));
    ui.set("accentColor", u["accentColor"].asString());
    ui.set("title", u["title"].asString());
    ui.set("welcomeText", u["welcomeText"].asString());
    ui.set("finishText", u["finishText"].asString());
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
    if (mode_ == Mode::Install) {
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
            const std::wstring p = install::ExpandDataTemplate(tpl);
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

void Controller::Start(const install::InstallOptions& options, HWND owner) {
    {
        std::lock_guard lock(mutex_);
        if (running_) return;
        running_ = true;
        started_ = true;
        outcome_.reset();
        cancel_ = false;
        ::ResetEvent(doneEvent_.get());
    }
    if (thread_.joinable()) thread_.join();
    thread_ = std::thread([this, options, owner] { Run(options, owner); });
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
            wchar_t buffer[16] = {};
            ::GetEnvironmentVariableW(L"FLUENTSETUP_TEST_FAULT_AFTER", buffer, 16);
            engine.SetFaultAfterFiles(static_cast<unsigned>(_wtoi(buffer)));
        }
        outcome = mode_ == Mode::Install ? engine.Install(options, sink, cancel_) : engine.Uninstall(args_.originDir, sink, cancel_, options.removeUserData);
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
    run.set("op", op ? op : mode_ == Mode::Uninstall ? "uninstall" : "install");
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
    m.set("retryable", IsRetryable(o.code));
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
