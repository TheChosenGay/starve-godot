#!/usr/bin/env bash
# 序号锚定（组件模式）端到端扫描：**真 gate + 无头协议客户端**，上行加受损（延迟/抖动/丢包/乱序）。
#
# 为什么这么做：单元/仿真测试用的是模拟服务端，这里要的是"真服务端 + 真协议 + 真组件"，
# 而且只测端的预测与服务端的配合 —— **不启动 Godot**。
#
# 用法：
#   scripts/netcode-sweep.sh            # 自己起一个临时 gate（全新存档），跑完自动停
#   STARVE_GATE=/tmp/starve-gate scripts/netcode-sweep.sh
set -uo pipefail
GATE="${STARVE_GATE:-/tmp/starve-gate}"
PORT="${STARVE_SWEEP_PORT:-18099}"
URL="ws://127.0.0.1:${PORT}/ws"
SAVE="$(mktemp -t starve-sweep-XXXXXX).bin"
LOG="$(mktemp -t starve-sweep-gate-XXXXXX).log"

cleanup() { [ -n "${gate_pid:-}" ] && kill "$gate_pid" 2>/dev/null; rm -f "$SAVE"; }
trap cleanup EXIT

if [ ! -x "$GATE" ]; then
  echo "找不到 gate 可执行文件 $GATE（先 go build -o $GATE ./cmd/gate，或设 STARVE_GATE）" >&2
  exit 2
fi

# 每次全新存档：避免世界演化后，冒烟里与网络无关的模板校验（花/资源）误报
# gate 要读它自己仓库里的 configs/（相对工作目录），所以在服务端目录里起
SERVER_DIR="${STARVE_SERVER_DIR:-../starve}"
( cd "$SERVER_DIR" && GATE_WS_ADDR="127.0.0.1:${PORT}" \
  GATE_METRICS_ADDR="127.0.0.1:$((PORT+1000))" \
  GATE_SAVE_FILE="$SAVE" "$GATE" ) > "$LOG" 2>&1 &
gate_pid=$!
for _ in $(seq 1 120); do
  grep -q "websocket server starting" "$LOG" && break
  sleep 0.5
done
if ! grep -q "websocket server starting" "$LOG"; then
  echo "gate 没起来，日志：" >&2; tail -5 "$LOG" >&2; exit 2
fi
SEED="${SEED:-11}"
pass=0; fail=0

run() {
  local label="$1"; shift
  local out
  out=$(dotnet run --project ProtocolSmoke/ProtocolSmoke.csproj -- \
        --netcode --url "$URL" --timeout-seconds 60 --seed "$SEED" "$@" 2>&1)
  local line; line=$(echo "$out" | grep -E "序号锚定" | head -1)
  if echo "$out" | grep -q "序号锚定 E2E 通过"; then
    pass=$((pass+1)); printf "PASS  %-42s %s\n" "$label" "$line"
  else
    fail=$((fail+1)); printf "FAIL  %-42s %s\n" "$label" "$line"
    echo "$out" | grep -E "^\[失败\]" | sed 's/^/      /'
  fi
}

run "干净链路"                                     --latency-ms 0
run "50ms"                                        --latency-ms 50
run "100ms"                                       --latency-ms 100
run "200ms + 20ms 抖动"                            --latency-ms 200 --jitter-ms 20
run "300ms + 50ms 抖动"                            --latency-ms 300 --jitter-ms 50
run "100ms + 30% 丢包"                             --latency-ms 100 --loss-percent 30
run "200ms+40ms 抖动+20% 丢+20% 乱序"               --latency-ms 200 --jitter-ms 40 --loss-percent 20 --reorder-percent 20
run "300ms+50ms 抖动+40% 丢+30% 乱序（极端）"        --latency-ms 300 --jitter-ms 50 --loss-percent 40 --reorder-percent 30

echo
echo "结果：通过 ${pass}，失败 ${fail}"
[ "$fail" -eq 0 ]
