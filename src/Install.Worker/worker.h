// Elevated worker mode of the stub ("--worker"). Started through UAC by the non-elevated controller for per-machine
// work. It connects back to the controller's pipe (verifying the server is its parent), accepts exactly one "run"
// request for the package embedded in its *own* executable, streams progress and exits. It never takes paths of
// files to copy, commands, or registry keys from the pipe: the only inputs are validated install options.
#pragma once

#include "Archive/container.h"
#include "Bootstrapper/args.h"
#include "Install.Core/package.h"

namespace fsetup::worker {

int RunWorker(const boot::Args& args, const install::Package& pkg, const archive::Container& container);

}  // namespace fsetup::worker
