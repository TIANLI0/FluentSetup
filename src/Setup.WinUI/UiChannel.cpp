#include "pch.h"

#include "UiChannel.h"

#include "IPC/protocol.h"

namespace fsui {

UiChannel& UiChannel::Get() {
    static UiChannel instance;
    return instance;
}

bool UiChannel::Connect(const std::wstring& pipeName, DWORD expectedServerPid) {
    channel_ = fsetup::ipc::Channel::Connect(pipeName, fsetup::ipc::kConnectTimeoutMs);
    if (!channel_.valid()) return false;
    // Refuse to talk to anything but the bootstrapper that launched us (defeats pipe-name squatting).
    if (channel_.PeerProcessId() != expectedServerPid) {
        channel_.Close();
        return false;
    }
    return true;
}

void UiChannel::SetPreview(fsetup::json::Value hello) {
    preview_ = true;
    hello_ = std::move(hello);
}

bool UiChannel::ReceiveHello(DWORD timeoutMs) {
    fsetup::json::Value message;
    if (!channel_.Receive(message, timeoutMs)) return false;
    if (message["t"].asString() != "hello" || message["proto"].asInt() != fsetup::ipc::kProtocolVersion) return false;
    hello_ = std::move(message);
    return true;
}

void UiChannel::StartReader(std::function<void(fsetup::json::Value)> onMessage, std::function<void()> onDisconnected) {
    if (preview_ || reader_.joinable()) return;
    reader_ = std::thread([this, onMessage = std::move(onMessage), onDisconnected = std::move(onDisconnected)] {
        for (;;) {
            fsetup::json::Value message;
            if (!channel_.Receive(message)) break;
            onMessage(std::move(message));
        }
        if (!stopping_) onDisconnected();
    });
}

void UiChannel::Send(const fsetup::json::Value& message) {
    if (!preview_) channel_.Send(message);
}

void UiChannel::SendType(const char* type) {
    fsetup::json::Value m;
    m.set("t", type);
    Send(m);
}

void UiChannel::Shutdown() {
    stopping_ = true;
    channel_.Close();
    if (reader_.joinable()) reader_.detach();  // the reader unblocks on the closed handle; never join from the UI thread
}

}  // namespace fsui
