.PHONY: check restore build test contract-check e2e netcode-sweep run run-capture run-smoke

STARVE_SERVER_DIR ?= ../starve
GATE_URL ?= ws://localhost:8081/ws
# 本机 Godot 不在 PATH 时用 app 内的可执行文件（macOS .NET 版）。
#
# 注意 `arch -arm64`：Godot .app 是 universal 二进制，若当前 shell 跑在
# Rosetta 下（sysctl.proc_translated=1），它会启动 x86_64 那一份，而
# Homebrew/官方安装的 .NET 运行时通常是 arm64-only —— 结果是启动即崩，
# 报错却是 libhostfxr.dylib "incompatible architecture (have 'arm64',
# need 'x86_64')"，看起来像依赖装错，实际是架构选错。
# 显式指定 arm64 可避免（Intel Mac 上无副作用：arch 会忽略该参数）。
GODOT ?= arch -arm64 /Applications/Godot_mono.app/Contents/MacOS/Godot

check: restore build test contract-check

restore:
	dotnet restore Starve.Core.Tests/Starve.Core.Tests.csproj
	dotnet restore Starve.Netcode.Tests/Starve.Netcode.Tests.csproj
	dotnet restore GodotClient/GodotClient.csproj
	dotnet restore ProtocolSmoke/ProtocolSmoke.csproj

build:
	dotnet build GodotClient/GodotClient.csproj --no-restore
	dotnet build ProtocolSmoke/ProtocolSmoke.csproj --no-restore

test:
	dotnet test Starve.Core.Tests/Starve.Core.Tests.csproj --no-restore
	dotnet test Starve.Netcode.Tests/Starve.Netcode.Tests.csproj --no-restore

contract-check:
	python3 scripts/check_proto_sync.py --server-dir "$(STARVE_SERVER_DIR)"

# 跑客户端（Godot 引擎）：dotnet build 只编译程序集，不会开窗口
run: build
	STARVE_GATE_URL="$(GATE_URL)" $(GODOT) --path GodotClient

# 3 秒后截图退出（不用手点）：验证渲染/调试碰撞体
run-capture: build
	STARVE_GATE_URL="$(GATE_URL)" $(GODOT) --path GodotClient -- --capture /tmp/starve-client.png
	@echo "截图：/tmp/starve-client.png"

# 连服冒烟（打印地图/实体数后退出）
run-smoke: build
	STARVE_GATE_URL="$(GATE_URL)" $(GODOT) --path GodotClient -- --smoke

# 序号锚定端到端扫描：**真 gate + 无头协议客户端**（不启动 Godot），上行加受损
# （延迟/抖动/丢包/乱序），逐场景断言"客户端预测 + 服务端配合"是否成立。
netcode-sweep: build
	cd "$(STARVE_SERVER_DIR)" && go build -o /tmp/starve-gate ./cmd/gate
	STARVE_GATE=/tmp/starve-gate STARVE_SERVER_DIR="$(STARVE_SERVER_DIR)" scripts/netcode-sweep.sh

# 独立集成门禁：调用方负责启动临时 gate；本地 make check 不依赖运行中的服务端。
e2e:
	dotnet run --project ProtocolSmoke/ProtocolSmoke.csproj -- --e2e
