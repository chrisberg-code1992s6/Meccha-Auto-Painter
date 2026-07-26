#pragma once

#include <cstddef>
#include <cstdint>
#include <type_traits>

#include <windows.h>

// This ABI is deliberately pointer-free. The injector copies the whole block into
// the target process, BridgeStartV1 copies it into bridge-owned state, and then the
// injector may safely reclaim the remote block after the remote start thread exits.
// Keep the fixed offsets in sync with the controller serializer.
constexpr std::uint32_t BridgeStartMagicV1 = 0x3153434D; // bytes: "MCS1"
constexpr std::uint32_t BridgeStartAbiV1 = 1;
constexpr std::uint32_t BridgeBootstrapProtocolV1 = 1;

enum BridgeStartResultV1 : std::uint32_t
{
    BRIDGE_START_UNINITIALIZED = 0,
    BRIDGE_START_STARTING = 1,
    BRIDGE_START_LISTENING = 2,
    BRIDGE_START_INVALID_BLOCK = 3,
    BRIDGE_START_PROCESS_MISMATCH = 4,
    BRIDGE_START_ALREADY_STARTED = 5,
    BRIDGE_START_WINSOCK_FAILED = 6,
    BRIDGE_START_SOCKET_FAILED = 7,
    BRIDGE_START_BIND_FAILED = 8,
    BRIDGE_START_LISTEN_FAILED = 9,
    BRIDGE_START_WORKER_FAILED = 10,
};

#pragma pack(push, 1)
struct BridgeStartBlockV1
{
    std::uint32_t magic;
    std::uint32_t size;
    std::uint32_t abi;
    std::uint32_t pid;
    std::uint8_t instance_guid[16];
    std::uint8_t token[32];
    std::uint8_t sha256[32];
    std::uint32_t requested_port;
    std::uint32_t result_state;
    std::uint32_t bound_port;
    std::uint32_t protocol;
    std::uint32_t win32_error;
    std::uint32_t winsock_error;
    std::uint32_t reserved0;
    std::uint32_t reserved1;
};
#pragma pack(pop)

using BridgeStartV1Fn = DWORD(WINAPI*)(void*);

static_assert(std::is_standard_layout_v<BridgeStartBlockV1>);
static_assert(sizeof(BridgeStartBlockV1) == 128);
static_assert(offsetof(BridgeStartBlockV1, magic) == 0);
static_assert(offsetof(BridgeStartBlockV1, size) == 4);
static_assert(offsetof(BridgeStartBlockV1, abi) == 8);
static_assert(offsetof(BridgeStartBlockV1, pid) == 12);
static_assert(offsetof(BridgeStartBlockV1, instance_guid) == 16);
static_assert(offsetof(BridgeStartBlockV1, token) == 32);
static_assert(offsetof(BridgeStartBlockV1, sha256) == 64);
static_assert(offsetof(BridgeStartBlockV1, requested_port) == 96);
static_assert(offsetof(BridgeStartBlockV1, result_state) == 100);
static_assert(offsetof(BridgeStartBlockV1, bound_port) == 104);
static_assert(offsetof(BridgeStartBlockV1, protocol) == 108);
static_assert(offsetof(BridgeStartBlockV1, win32_error) == 112);
static_assert(offsetof(BridgeStartBlockV1, winsock_error) == 116);
static_assert(offsetof(BridgeStartBlockV1, reserved0) == 120);
static_assert(offsetof(BridgeStartBlockV1, reserved1) == 124);
