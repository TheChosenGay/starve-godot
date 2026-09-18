#!/bin/sh
# 双击即跑：跨仓语料对齐本地控制台（macOS / Linux）
#
# Finder 双击时 PATH 非常干净（通常只有 /usr/bin:/bin:/usr/sbin:/sbin），
# Homebrew 的 python3 不在里面，所以这里按常见位置自己找一遍。

cd "$(dirname "$0")/.." || exit 2

PY=""
for cand in python3 /opt/homebrew/bin/python3 /usr/local/bin/python3 /usr/bin/python3; do
    if command -v "$cand" >/dev/null 2>&1; then
        PY="$cand"
        break
    fi
done

if [ -z "$PY" ]; then
    echo "找不到 python3。请先安装 Python 3.8+（https://www.python.org/downloads/）后重试。"
    printf "\n按回车键关闭此窗口…"
    read -r _
    exit 2
fi

echo "使用解释器：$PY"
"$PY" -u scripts/corpus_align_ui.py "$@"
status=$?

# 双击场景下留住窗口，方便看报错；从终端调用且带管道时不拦。
if [ -t 0 ]; then
    printf "\n进程已退出（退出码 %s）。按回车键关闭此窗口…" "$status"
    read -r _
fi
exit $status
