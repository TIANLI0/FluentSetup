// A minimal fork-join loop for file-system work. Creating, renaming and deleting files costs a near-constant latency
// per file (the file system and its filter drivers, most of all real-time virus scanning), not CPU, so issuing these
// calls from a few threads at once hides most of it. Header-only; no thread outlives the call.
#pragma once

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <exception>
#include <mutex>
#include <system_error>
#include <thread>
#include <vector>

namespace fsetup {

// Threads for per-file I/O: at least 4 even on small machines (the threads mostly wait), at most 8 (more only adds
// contention in the file system).
inline unsigned IoThreads() noexcept {
    const unsigned hw = std::max(1u, std::thread::hardware_concurrency());
    return std::clamp(hw, 4u, 8u);
}

// Calls fn(i) for every i in [0, count), on up to `threads` threads including the caller. The order is unspecified.
// The first exception stops further items from starting; it is rethrown once every thread has finished.
template <class Fn>
void ParallelFor(size_t count, Fn&& fn, unsigned threads = IoThreads()) {
    if (count == 0) return;
    threads = static_cast<unsigned>(std::min<size_t>(threads, count));
    if (threads <= 1) {
        for (size_t i = 0; i < count; ++i) fn(i);
        return;
    }
    std::atomic<size_t> next{0};
    std::atomic<bool> failed{false};
    std::exception_ptr error;
    std::mutex errorLock;
    const auto body = [&] {
        while (!failed.load(std::memory_order_relaxed)) {
            const size_t i = next.fetch_add(1, std::memory_order_relaxed);
            if (i >= count) return;
            try {
                fn(i);
            } catch (...) {
                std::lock_guard lock(errorLock);
                if (!error) error = std::current_exception();
                failed = true;
                return;
            }
        }
    };
    std::vector<std::thread> pool;
    pool.reserve(threads - 1);
    try {
        for (unsigned t = 1; t < threads; ++t) pool.emplace_back(body);
    } catch (const std::system_error&) {
        // Fewer threads than asked for: the ones that started (and this one) do all the work.
    }
    body();
    for (auto& t : pool) t.join();
    if (error) std::rethrow_exception(error);
}

}  // namespace fsetup
