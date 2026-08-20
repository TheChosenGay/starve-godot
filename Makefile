.PHONY: check restore build test contract-check e2e

STARVE_SERVER_DIR ?= ../starve

check: restore build test contract-check

restore:
	dotnet restore Starve.Core.Tests/Starve.Core.Tests.csproj
	dotnet restore GodotClient/GodotClient.csproj
	dotnet restore ProtocolSmoke/ProtocolSmoke.csproj

build:
	dotnet build GodotClient/GodotClient.csproj --no-restore
	dotnet build ProtocolSmoke/ProtocolSmoke.csproj --no-restore

test:
	dotnet test Starve.Core.Tests/Starve.Core.Tests.csproj --no-restore

contract-check:
	python3 scripts/check_proto_sync.py --server-dir "$(STARVE_SERVER_DIR)"

# 独立集成门禁：调用方负责启动临时 gate；本地 make check 不依赖运行中的服务端。
e2e:
	dotnet run --project ProtocolSmoke/ProtocolSmoke.csproj -- --e2e
