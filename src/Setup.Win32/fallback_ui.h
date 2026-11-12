// Win32 fallback front end. Same pages and decisions as the WinUI front end, built from system controls only
// (Common Controls v6, UxTheme, DWM). Used when the Windows App Runtime is missing/incompatible, when WinUI fails to
// start, or when the WinUI process stops mid-session (then it resumes the running session).
#pragma once

#include "Bootstrapper/controller.h"

namespace fsetup::win32ui {

void Run(boot::Controller& controller, bool resume);

}  // namespace fsetup::win32ui
