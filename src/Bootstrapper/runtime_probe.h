// Quick, side-effect-free check whether a Windows App Runtime compatible with the bundled WinUI front end is installed
// for this process architecture. It only decides whether trying WinUI is worthwhile; the authoritative check is the
// MddBootstrapInitialize2 call inside the UI process, reported back through the handshake.
#pragma once

#include <string>

namespace fsetup::boot {

struct RuntimeProbe {
    bool compatible = false;
    std::wstring detail;  // for the log: what was found / why it was rejected
};

RuntimeProbe ProbeWindowsAppRuntime();

}  // namespace fsetup::boot
