#!/usr/bin/env python3
"""跨仓"移动/碰撞一致性语料"对齐运行器（本地与 CI 同一入口）。

## 它做什么

1. 一致性守卫：两仓 `testdata/move_corpus.jsonl` 必须**逐字节一致**（不一致 = 各跑各的，白测）。
2. 服务端回放：在服务端目录跑 `go test -run TestMoveCorpusVectors`（用**真实求解路径**逐条比对期望位移）。
3. 客户端回放：在客户端目录跑 `dotnet test --filter MovementCorpusTests`（同一份语料逐条回放）。
4. 汇总：每步 PASS/FAIL + 耗时；任一步失败则非零退出。

## 为什么这样能保证两端一致（原理）

两端的移动都是同一个**确定性纯函数**：`下一状态 = step(状态, 操作, dt, 世界输入)`
（不依赖时钟、随机数、外部 IO）。于是"两端一致"本质是**函数相等**，而语料把它变成可判定的事实：
一组固定的「场景输入 → 期望位移」，两端各自跑自己的实现去比：

* **期望值由权威侧生成**：生成器直接调服务端真实求解代码（不是另写一套公式）⇒ 语料本身即 ground truth，
  客户端是被校验方，"接近但不相等"这种会持续制造校正的漂移无法潜伏。
* **覆盖的是分支交界**（恰好接触、贴墙 0.999/0.001、对角线归一化、半径和恰好等于距离、
  ORCA 迎面/追尾/三体退化、并列打破规则）——数学主体好对齐，分歧都藏在边界上。
* **两道护栏**：① 本脚本第 1 步保证两仓语料逐字节一致；② 容差与浮点约定（float32/float64、运算顺序）
  写在语料测试与 docs/P1.4 里，不放宽容差来"变绿"。

## 用法

本地（两个仓库是兄弟目录时，零参数可用）：

    python3 scripts/corpus_align.py
    python3 scripts/corpus_align.py --server-dir ../starve --client-dir .

CI（与服务端 checkout 路径无关，同一入口）：

    python3 scripts/corpus_align.py --server-dir .contract-server --client-dir .

重新生成语料（改过移动数学/碰撞/ORCA/坡度之后；生成后请人工确认差异可解释）：

    python3 scripts/corpus_align.py --regen --seed 11 --count 400

机器可读报告：

    python3 scripts/corpus_align.py --json /tmp/corpus-report.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

CORPUS_REL = Path("testdata/move_corpus.jsonl")
SERVER_TEST = "TestMoveCorpusVectors"
SERVER_TEST_PKG = "./cmd/movecorpus/"   # 生成器与回放测试同包：保证"期望值"与"回放"共用同一条真实求解路径
CLIENT_TEST_FILTER = "FullyQualifiedName~MovementCorpusTests"
GEN_CMD = ["go", "run", "./cmd/movecorpus"]


class Step:
    """一步的结果（给汇总表和 --json 用）。"""

    def __init__(self, name: str) -> None:
        self.name = name
        self.ok = False
        self.detail = ""
        self.seconds = 0.0


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    h.update(path.read_bytes())
    return h.hexdigest()


def count_lines(path: Path) -> int:
    with path.open("r", encoding="utf-8") as fh:
        return sum(1 for line in fh if line.strip())


def first_difference(a: Path, b: Path) -> str:
    """返回首个差异的可读描述（行号 + 两侧内容），用于不一致时定位。"""
    la = a.read_text(encoding="utf-8").replace("\r\n", "\n").splitlines()
    lb = b.read_text(encoding="utf-8").replace("\r\n", "\n").splitlines()
    for i in range(max(len(la), len(lb))):
        x = la[i] if i < len(la) else "<缺行>"
        y = lb[i] if i < len(lb) else "<缺行>"
        if x != y:
            return (
                f"第 {i + 1} 行不同：\n"
                f"      服务端: {x[:200]}\n"
                f"      客户端: {y[:200]}"
            )
    return "行内容相同（差异在行尾换行/编码）"


def run(cmd: list[str], cwd: Path, timeout: int) -> tuple[int, str]:
    proc = subprocess.run(
        cmd, cwd=str(cwd), capture_output=True, text=True, timeout=timeout
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    return proc.returncode, out


def tail(text: str, lines: int = 12) -> str:
    rows = [r for r in text.strip().splitlines() if r.strip()]
    return "\n      ".join(rows[-lines:])


def check_layout(server: Path, client: Path) -> str | None:
    """轻量环境检查：路径像不像这两个仓库。返回错误信息（None = OK）。"""
    if not (server / "go.mod").is_file():
        return f"服务端目录不像 Go 仓库（缺 go.mod）：{server}"
    if not (server / "cmd" / "movecorpus").is_dir():
        return f"服务端缺生成器 cmd/movecorpus：{server}（先实现语料生成器）"
    if not (client / "Starve.Core.Tests").is_dir():
        return f"客户端目录不像本仓库（缺 Starve.Core.Tests）：{client}"
    return None


def step_sync(server: Path, client: Path, step: Step) -> None:
    a, b = server / CORPUS_REL, client / CORPUS_REL
    if not a.is_file() or not b.is_file():
        missing = a if not a.is_file() else b
        step.detail = f"语料缺失：{missing}（用 --regen 生成）"
        return
    same = sha256(a) == sha256(b)
    count = count_lines(a)
    step.detail = f"{count} 条；服务端 {sha256(a)[:12]} / 客户端 {sha256(b)[:12]}"
    if not same:
        step.detail += f"\n      {first_difference(a, b)}"
        return
    step.ok = True


def step_regen(server: Path, client: Path, seed: int, count: int, timeout: int, step: Step) -> None:
    cmd = GEN_CMD + ["-seed", str(seed), "-count", str(count), "-out", str(CORPUS_REL)]
    code, out = run(cmd, server, timeout)
    if code != 0:
        step.detail = f"生成失败（exit={code}）：\n      {tail(out, 8)}"
        return
    dst = client / CORPUS_REL
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(server / CORPUS_REL, dst)
    step.ok = True
    step.detail = f"seed={seed} count={count} → {CORPUS_REL}（已复制到客户端）；{tail(out, 3)}"


def step_server_test(server: Path, timeout: int, step: Step) -> None:
    cmd = ["go", "test", "-count=1", "-run", SERVER_TEST, "-v", SERVER_TEST_PKG]
    code, out = run(cmd, server, timeout)
    step.ok = code == 0
    step.detail = tail(out, 10) if code != 0 else tail(out, 4)


def step_client_test(client: Path, timeout: int, step: Step) -> None:
    cmd = [
        "dotnet", "test", "Starve.Core.Tests/Starve.Core.Tests.csproj",
        "--filter", CLIENT_TEST_FILTER, "--nologo", "-v", "q",
    ]
    code, out = run(cmd, client, timeout)
    step.ok = code == 0
    step.detail = tail(out, 12) if code != 0 else tail(out, 3)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="跨仓移动/碰撞一致性语料对齐（本地与 CI 同一入口）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--server-dir", default="../starve", help="服务端仓库路径（默认 ../starve；CI 里是 .contract-server）")
    ap.add_argument("--client-dir", default=".", help="客户端仓库路径（默认当前目录）")
    ap.add_argument("--regen", action="store_true", help="先用服务端生成器重新生成语料，再复制到客户端并继续对齐")
    ap.add_argument("--seed", type=int, default=11, help="--regen 的随机种子（默认 11）")
    ap.add_argument("--count", type=int, default=400, help="--regen 的场景条数（默认 400）")
    ap.add_argument("--timeout", type=int, default=1800, help="单步超时秒数（默认 1800）")
    ap.add_argument("--json", dest="json_path", help="把结果写成 JSON（给工具/CI 消费）")
    args = ap.parse_args()

    server = Path(args.server_dir).resolve()
    client = Path(args.client_dir).resolve()

    print("== 跨端一致性语料对齐 ==")
    print(f"   服务端: {server}")
    print(f"   客户端: {client}")

    problem = check_layout(server, client)
    if problem:
        print(f"\n[FAIL] 环境检查：{problem}")
        print("       提示：本地默认按兄弟目录 ../starve 与当前目录；CI 里服务端通常 checkout 到 .contract-server")
        return 2

    steps: list[Step] = []

    if args.regen:
        s = Step("重新生成语料（服务端权威侧 → 复制到客户端）")
        t0 = time.time()
        step_regen(server, client, args.seed, args.count, args.timeout, s)
        s.seconds = time.time() - t0
        steps.append(s)
        if not s.ok:
            return finish(steps, args)
        print(f"\n[ OK ] {s.name}  ({s.seconds:.1f}s)\n       {s.detail}")

    for name, fn in (
        ("两仓语料逐字节一致", lambda st: step_sync(server, client, st)),
        (f"服务端回放（go test -run {SERVER_TEST} {SERVER_TEST_PKG}）", lambda st: step_server_test(server, args.timeout, st)),
        (f"客户端回放（dotnet test --filter {CLIENT_TEST_FILTER}）", lambda st: step_client_test(client, args.timeout, st)),
    ):
        s = Step(name)
        t0 = time.time()
        try:
            fn(s)
        except subprocess.TimeoutExpired:
            s.ok, s.detail = False, f"超时（>{args.timeout}s）"
        except FileNotFoundError as exc:
            s.ok, s.detail = False, f"命令不存在：{exc}"
        s.seconds = time.time() - t0
        steps.append(s)
        tag = " OK " if s.ok else "FAIL"
        print(f"\n[{tag}] {s.name}  ({s.seconds:.1f}s)\n       {s.detail}")
        if not s.ok:
            break

    return finish(steps, args)


def finish(steps: list[Step], args: argparse.Namespace) -> int:
    ok = all(s.ok for s in steps) and len(steps) >= 3
    print("\n== 汇总 ==")
    for s in steps:
        print(f"   {'PASS' if s.ok else 'FAIL'}  {s.seconds:6.1f}s  {s.name}")
    print(f"\n结论：{'两端语料对齐通过 ✓' if ok else '未通过 ✗（见上面失败步的详情）'}")
    if args.json_path:
        Path(args.json_path).write_text(
            json.dumps(
                {
                    "ok": ok,
                    "server_dir": args.server_dir,
                    "client_dir": args.client_dir,
                    "steps": [
                        {"name": s.name, "ok": s.ok, "seconds": round(s.seconds, 2), "detail": s.detail}
                        for s in steps
                    ],
                },
                ensure_ascii=False,
                indent=2,
            ),
            encoding="utf-8",
        )
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
