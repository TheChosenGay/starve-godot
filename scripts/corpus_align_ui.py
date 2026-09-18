#!/usr/bin/env python3
"""跨仓"移动/碰撞一致性语料对齐"的本地图形控制台（只用 Python 标准库）。

## 为什么是"本地 web UI"而不是 Tkinter

本机 `python3` 是 Homebrew 版，`_tkinter` 走不了（实测 `import tkinter` 失败），
所以 UI 用标准库的 `http.server` 在 **127.0.0.1** 上起一个只监听回环的小服务，
再用系统默认浏览器打开它。前端 HTML/CSS/JS 全部内嵌，**不引任何外网资源**。

## 它不重写任何对齐逻辑

「跑对齐」「重新生成语料」都是去 **subprocess 调用 `scripts/corpus_align.py`**，
和 CI / `make corpus-align` 是同一个入口。本文件只做三件事：
把参数传进去、把 stdout/stderr 实时透出来、把 `--json` 报告渲染成表。

## 接口

    GET  /                     页面
    GET  /api/detect           探测候选仓库路径 + 上次选择（~/.starve-corpus-align.json）
    GET  /api/ls?path=<dir>    列目录（浏览器拿不到本地 FS，所以由本地服务代劳）
    GET  /api/log?since=<n>    增量日志
    GET  /api/status           运行状态 + 结构化报告
    POST /api/run              跑对齐
    POST /api/regen            重新生成语料（等价 --regen --seed --count）
    POST /api/stop             终止当前子进程（含子进程树）

## 用法

    python3 scripts/corpus_align_ui.py
    python3 scripts/corpus_align_ui.py --port 8765 --no-open
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import signal
import string
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HOST = "127.0.0.1"  # 只监听本地回环，绝不对外
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
CLI = SCRIPT_DIR / "corpus_align.py"
STATE_FILE = Path.home() / ".starve-corpus-align.json"
STATE_KEYS = ("server_dir", "client_dir", "seed", "count")
MAX_LOG_LINES = 20000
DEFAULT_SEED = 11
DEFAULT_COUNT = 400
DEFAULT_TIMEOUT = 1800


# --------------------------------------------------------------------------- #
# 上次选择（~/.starve-corpus-align.json）
# --------------------------------------------------------------------------- #
def load_state() -> dict:
    state = {"server_dir": "", "client_dir": "", "seed": DEFAULT_SEED, "count": DEFAULT_COUNT}
    try:
        raw = json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return state
    if not isinstance(raw, dict):
        return state
    for key in STATE_KEYS:
        if key in raw and raw[key] not in (None, ""):
            state[key] = raw[key]
    return state


def save_state(state: dict) -> None:
    payload = {key: state.get(key) for key in STATE_KEYS}
    try:
        STATE_FILE.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    except OSError as exc:  # 写不了也不该让 UI 挂掉
        print(f"[ui] 警告：无法写入 {STATE_FILE}：{exc}", file=sys.stderr)


# --------------------------------------------------------------------------- #
# 路径探测
# --------------------------------------------------------------------------- #
def probe(path: Path) -> dict:
    """看一眼这个目录像不像那两个仓库之一。"""
    try:
        resolved = Path(path).expanduser().resolve()
    except OSError:
        resolved = Path(path).expanduser()
    try:
        exists = resolved.is_dir()
    except OSError:
        exists = False
    has_go_mod = exists and (resolved / "go.mod").is_file()
    has_movecorpus = exists and (resolved / "cmd" / "movecorpus").is_dir()
    has_client_tests = exists and (resolved / "Starve.Core.Tests").is_dir()
    if has_go_mod and not has_client_tests:
        role = "server"
    elif has_client_tests and not has_go_mod:
        role = "client"
    elif has_go_mod and has_client_tests:
        role = "either"
    else:
        role = "unknown"
    return {
        "path": str(resolved),
        "exists": exists,
        "has_go_mod": has_go_mod,
        "has_movecorpus": has_movecorpus,
        "has_client_tests": has_client_tests,
        "role": role,
    }


def pretty_label(path: Path, home: Path, parent: Path) -> str:
    try:
        resolved = Path(path).expanduser().resolve()
    except OSError:
        return str(path)
    if resolved == home:
        return "~/"
    if resolved == parent:
        return "../"
    try:
        rel_home = resolved.relative_to(home)
        return "~/" + str(rel_home)
    except ValueError:
        pass
    if resolved.parent == parent:
        return f"../{resolved.name}"
    return str(resolved)


def detect_candidates() -> list:
    """候选路径：真实存在的优先，按"像不像仓库"排序。"""
    home = Path.home()
    parent = REPO_ROOT.parent
    out = []
    seen = set()

    def add(path: Path) -> None:
        info = probe(path)
        key = info["path"]
        if key in seen:
            return
        seen.add(key)
        info["label"] = pretty_label(path, home, parent)
        out.append(info)

    add(REPO_ROOT)                       # ./ 客户端仓库根
    add(parent)                          # ../
    add(parent / "starve")               # ../starve
    add(parent / "starve-godot")
    add(home / "starve")
    add(home / "starve-godot")
    add(REPO_ROOT / ".contract-server")  # CI 里服务端 checkout 到这里
    add(Path.cwd())

    # 浅扫兄弟目录，把真正像仓库的也摆出来（最多 200 个条目）
    try:
        children = sorted(parent.iterdir(), key=lambda c: c.name.lower())[:200]
    except OSError:
        children = []
    for child in children:
        if child.name.startswith("."):
            continue
        try:
            if not child.is_dir():
                continue
        except OSError:
            continue
        info = probe(child)
        if info["role"] != "unknown":
            add(child)

    order = {"server": 0, "client": 1, "either": 2, "unknown": 3}
    out.sort(key=lambda c: (not c["exists"], order.get(c["role"], 9), c["label"]))
    return out


# --------------------------------------------------------------------------- #
# 子进程运行器
# --------------------------------------------------------------------------- #
STEP_HEAD = re.compile(r"^\s*\[( OK |FAIL)\]\s*(.*?)\s*$")
STEP_HEAD_SECONDS = re.compile(r"^(.*?)\s*\((\d+(?:\.\d+)?)s\)\s*$")


def preflight(server: str, client: str) -> list:
    """给用户即时可读的提示；最终判定仍以 CLI 输出为准。"""
    warnings = []
    sp, cp = Path(server).expanduser(), Path(client).expanduser()
    try:
        if not sp.is_dir():
            warnings.append(f"服务端路径不存在或不是目录：{server}")
        else:
            if not (sp / "go.mod").is_file():
                warnings.append(f"服务端目录缺 go.mod，不像 Go 仓库：{server}")
            if not (sp / "cmd" / "movecorpus").is_dir():
                warnings.append(f"服务端缺生成器 cmd/movecorpus：{server}（语料生成器可能尚未实现）")
    except OSError as exc:
        warnings.append(f"无法访问服务端路径 {server}：{exc}")
    try:
        if not cp.is_dir():
            warnings.append(f"客户端路径不存在或不是目录：{client}")
        elif not (cp / "Starve.Core.Tests").is_dir():
            warnings.append(f"客户端目录缺 Starve.Core.Tests，不像本仓库：{client}")
    except OSError as exc:
        warnings.append(f"无法访问客户端路径 {client}：{exc}")
    return warnings


def parse_log_report(lines: list, exit_code) -> dict:
    """CLI 在环境检查失败时直接 return 2（不写 --json），这里从日志兜底重建一份。"""
    steps = []
    current = None
    for item in lines:
        text = item["text"]
        match = STEP_HEAD.match(text)
        if match:
            tag, rest = match.group(1), match.group(2)
            name, seconds = rest, 0.0
            sub = STEP_HEAD_SECONDS.match(rest)
            if sub:
                name, seconds = sub.group(1), float(sub.group(2))
            current = {"name": name, "ok": tag == " OK ", "seconds": seconds, "detail": ""}
            steps.append(current)
            continue
        if current is not None:
            stripped = text.strip()
            if stripped and not stripped.startswith("==") and not stripped.startswith("["):
                current["detail"] = (current["detail"] + "\n" + stripped).strip()
    ok = exit_code == 0 and bool(steps) and all(s["ok"] for s in steps)
    return {"ok": ok, "server_dir": "", "client_dir": "", "steps": steps, "synthesized": True}


class Runner:
    """同一时刻只允许一个任务；日志用增量游标读。"""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._lines = collections.deque(maxlen=MAX_LOG_LINES)
        self._seq = 0
        self._proc = None
        self._running = False
        self._stopped = False
        self._exit_code = None
        self._started_at = None
        self._finished_at = None
        self._report = None
        self._report_source = None
        self._mode = None
        self._cmdline = ""
        self._server = ""
        self._client = ""
        self._json_path = None
        self._tmpdir = Path(tempfile.mkdtemp(prefix="starve-corpus-align-"))

    # ---------------- 日志 ----------------
    def _append(self, text: str, src: str = "out") -> None:
        with self._lock:
            self._lines.append({"n": self._seq, "src": src, "text": text})
            self._seq += 1

    def log_since(self, since: int):
        with self._lock:
            items = [e for e in self._lines if e["n"] >= since]
            nxt = self._seq
            oldest = self._lines[0]["n"] if self._lines else nxt
        return items, nxt, oldest

    def _reset_log(self) -> None:
        with self._lock:
            self._lines.clear()
            self._seq = 0

    # ---------------- 状态 ----------------
    def status(self, state: dict) -> dict:
        with self._lock:
            running = self._running
            return {
                "running": running,
                "mode": self._mode,
                "cmdline": self._cmdline,
                "server_dir": self._server,
                "client_dir": self._client,
                "exit_code": self._exit_code,
                "stopped": self._stopped,
                "ok": (self._report or {}).get("ok") if self._report else None,
                "started_at": self._started_at,
                "finished_at": self._finished_at,
                "elapsed": (time.time() - self._started_at) if (running and self._started_at) else None,
                "report": self._report,
                "report_source": self._report_source,
                "log_count": self._seq,
                "settings": {k: state.get(k) for k in STATE_KEYS},
                "state_file": str(STATE_FILE),
            }

    # ---------------- 启动 / 停止 ----------------
    def _build_cmd(self, mode, server, client, seed, count, timeout, json_path):
        cmd = [
            sys.executable, "-u", str(CLI),
            "--server-dir", server,
            "--client-dir", client,
            "--timeout", str(timeout),
            "--json", json_path,
        ]
        if mode == "regen":
            cmd += ["--regen", "--seed", str(seed), "--count", str(count)]
        return cmd

    def start(self, mode, server, client, seed, count, timeout, state) -> tuple:
        with self._lock:
            if self._running:
                return False, "已有任务在跑，请先等它结束或点「停止」"
        if not CLI.is_file():
            return False, f"找不到 CLI：{CLI}"

        json_path = str(self._tmpdir / "report.json")
        try:
            os.remove(json_path)
        except OSError:
            pass

        cmd = self._build_cmd(mode, server, client, seed, count, timeout, json_path)
        self._reset_log()
        self._append(
            "[ui] 模式：" + ("重新生成语料（--regen）" if mode == "regen" else "跑对齐"),
            "ui",
        )
        self._append("[ui] 命令：" + " ".join(cmd), "ui")
        for warn in preflight(server, client):
            self._append(f"[ui] 预检提示：{warn}", "ui")
        self._append("[ui] " + "-" * 60, "ui")

        kwargs = {"cwd": str(REPO_ROOT), "stdout": subprocess.PIPE,
                  "stderr": subprocess.STDOUT, "stdin": subprocess.DEVNULL,
                  "text": True, "bufsize": 1}
        if os.name == "nt":
            kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            kwargs["start_new_session"] = True  # 便于整组杀掉 go/dotnet 子进程
        try:
            proc = subprocess.Popen(cmd, **kwargs)
        except OSError as exc:
            self._append(f"[ui] 无法启动子进程：{exc}", "ui")
            return False, f"无法启动子进程：{exc}"

        with self._lock:
            self._proc = proc
            self._running = True
            self._stopped = False
            self._exit_code = None
            self._report = None
            self._report_source = None
            self._started_at = time.time()
            self._finished_at = None
            self._mode = mode
            self._cmdline = " ".join(cmd)
            self._server = server
            self._client = client
            self._json_path = json_path

        state["server_dir"], state["client_dir"] = server, client
        if mode == "regen":
            state["seed"], state["count"] = seed, count
        save_state(state)

        threading.Thread(target=self._pump, args=(proc, json_path), daemon=True).start()
        return True, None

    def _pump(self, proc, json_path) -> None:
        try:
            if proc.stdout is not None:
                for line in proc.stdout:
                    self._append(line.rstrip("\r\n"))
        except (OSError, ValueError) as exc:
            self._append(f"[ui] 读取输出中断：{exc}", "ui")
        code = proc.wait()

        report, source = None, None
        if os.path.isfile(json_path):
            try:
                report = json.loads(Path(json_path).read_text(encoding="utf-8"))
                source = "json"
            except (OSError, ValueError) as exc:
                self._append(f"[ui] --json 报告解析失败（{exc}），改用日志重建", "ui")
        if report is None:
            with self._lock:
                snapshot = list(self._lines)
            report = parse_log_report(snapshot, code)
            source = "log"

        with self._lock:
            self._running = False
            self._proc = None
            self._exit_code = code
            self._finished_at = time.time()
            stopped = self._stopped
            if stopped:
                self._report, self._report_source = None, None
            else:
                self._report, self._report_source = report, source
        if stopped:
            self._append("[ui] 任务已被手动停止，未产生结论。", "ui")
        else:
            self._append("[ui] " + "-" * 60, "ui")
            verdict = "通过" if report.get("ok") else "未通过"
            self._append(f"[ui] 子进程退出码={code}，结论：{verdict}（报告来源：{source}）", "ui")

    def stop(self, join: bool = False) -> bool:
        with self._lock:
            proc = self._proc
            if proc is None or not self._running:
                return False
            self._stopped = True
        self._kill_tree(proc)
        if join:
            try:
                proc.wait(timeout=5)
            except Exception:
                pass
        return True

    @staticmethod
    def _kill_tree(proc) -> None:
        """先礼后兵：SIGTERM/正常终止，5 秒不退再强杀整棵进程树。"""
        if os.name == "nt":
            try:
                subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                               capture_output=True, timeout=10)
            except Exception:
                try:
                    proc.terminate()
                except Exception:
                    pass
            return
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
        except Exception:
            try:
                proc.terminate()
            except Exception:
                pass
        try:
            proc.wait(timeout=5)
            return
        except Exception:
            pass
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass


# --------------------------------------------------------------------------- #
# 目录浏览（浏览器拿不到本地文件系统，所以由本地服务代劳）
# --------------------------------------------------------------------------- #
def list_roots() -> list:
    roots = []
    if os.name == "nt":
        for letter in string.ascii_uppercase:
            drive = Path(letter + ":\\")
            try:
                if drive.exists():
                    roots.append(str(drive))
            except OSError:
                continue
    else:
        roots.append("/")
    home = str(Path.home())
    if home not in roots:
        roots.append(home)
    return roots


def api_ls(raw_path: str) -> dict:
    roots = list_roots()
    text = (raw_path or "").strip()
    if not text:
        return {"ok": True, "path": None, "parent": None, "home": str(Path.home()),
                "roots": roots, "dirs": [], "message": "请选择起点目录"}
    target = Path(text).expanduser()
    try:
        if not target.exists():
            return {"ok": False, "error": f"目录不存在：{target}", "path": str(target),
                    "home": str(Path.home()), "roots": roots, "dirs": []}
        if not target.is_dir():
            return {"ok": False, "error": f"这不是目录：{target}", "path": str(target),
                    "home": str(Path.home()), "roots": roots, "dirs": []}
    except OSError as exc:
        return {"ok": False, "error": f"无法访问 {text}：{exc}", "path": text,
                "home": str(Path.home()), "roots": roots, "dirs": []}
    try:
        resolved = target.resolve()
    except OSError:
        resolved = target
    dirs = []
    try:
        for child in sorted(resolved.iterdir(), key=lambda c: c.name.lower()):
            if child.name.startswith("."):
                continue
            try:
                if child.is_dir():
                    dirs.append({"name": child.name, "path": str(child), "hidden": False})
            except OSError:
                continue
    except PermissionError:
        return {"ok": False, "error": f"没有权限读取：{resolved}", "path": str(resolved),
                "home": str(Path.home()), "roots": roots, "dirs": []}
    except OSError as exc:
        return {"ok": False, "error": f"读取目录失败：{resolved}（{exc}）", "path": str(resolved),
                "home": str(Path.home()), "roots": roots, "dirs": []}
    try:
        parent = str(resolved.parent) if resolved.parent != resolved else None
    except OSError:
        parent = None
    info = probe(resolved)
    return {"ok": True, "path": str(resolved), "parent": parent,
            "home": str(Path.home()), "roots": roots, "dirs": dirs,
            "role": info["role"], "is_server": info["role"] in ("server", "either"),
            "is_client": info["role"] in ("client", "either")}


# --------------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------------- #
INDEX_HTML = r"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>跨端语料对齐 · 本地控制台</title>
<style>
  :root{--bg:#0f1218;--card:#171c25;--line:#26303d;--fg:#e6edf3;--muted:#8b98a8;
        --accent:#3d8bfd;--ok:#2ea043;--bad:#da3633;--warn:#d29922;}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--fg);
       font:14px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif}
  .wrap{max-width:1080px;margin:0 auto;padding:18px 16px 40px}
  header h1{font-size:19px;margin:0 0 2px}
  header .sub{color:var(--muted);font-size:12px;margin-bottom:14px}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px;margin-bottom:12px}
  .card-title{font-weight:600;font-size:13px;color:var(--muted);margin-bottom:9px;
              display:flex;align-items:center;gap:8px;flex-wrap:wrap}
  .right{margin-left:auto;display:flex;align-items:center;gap:8px}
  .field{display:flex;align-items:center;gap:8px;margin-bottom:8px;flex-wrap:wrap}
  .field>label{width:88px;color:var(--muted);font-size:12px;flex:none}
  .field.small>label{width:88px}
  input[type=text],input[type=number]{background:#0c1016;border:1px solid var(--line);color:var(--fg);
        border-radius:6px;padding:7px 9px;font:13px/1.3 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}
  input[type=text]{flex:1;min-width:220px}
  input[type=number]{width:6rem}
  .btn{background:#222b37;border:1px solid var(--line);color:var(--fg);border-radius:6px;
       padding:7px 12px;font-size:13px;cursor:pointer}
  .btn:hover:not(:disabled){border-color:#41505f;background:#28323f}
  .btn:disabled{opacity:.45;cursor:not-allowed}
  .btn.primary{background:var(--accent);border-color:var(--accent);color:#fff;font-weight:600}
  .btn.warn{background:#3a2f12;border-color:#6b5518;color:#f0c674}
  .btn.danger{background:#3a1a1a;border-color:#6b2424;color:#ff9a95}
  .btn.tiny{padding:3px 8px;font-size:12px}
  .actions{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px}
  .chips{display:flex;gap:6px;flex-wrap:wrap}
  .chip{background:#0c1016;border:1px solid var(--line);border-radius:999px;padding:3px 10px;
        font-size:12px;cursor:pointer;font-family:ui-monospace,Menlo,Consolas,monospace}
  .chip:hover{border-color:var(--accent)}
  .chip .badge{font-size:10px;border-radius:4px;padding:0 5px;margin-left:6px;background:#2b3644;color:var(--muted)}
  .chip.server .badge{background:#12351f;color:#5ddb8a}
  .chip.client .badge{background:#122a44;color:#79b8ff}
  .chip.either .badge{background:#3a2f12;color:#f0c674}
  .muted{color:var(--muted);font-size:12px}
  .hint{margin-top:9px;font-size:12px;color:var(--warn);white-space:pre-wrap}
  .hint.err{color:#ff9a95}
  .hint.ok{color:#5ddb8a}
  .pill{border-radius:999px;padding:1px 9px;font-size:11px;font-weight:600}
  .pill.idle{background:#2b3644;color:var(--muted)}
  .pill.run{background:#122a44;color:#79b8ff}
  .pill.pass{background:#12351f;color:#5ddb8a}
  .pill.fail{background:#3a1a1a;color:#ff9a95}
  .conclusion{border-radius:8px;padding:12px 14px;font-size:15px;font-weight:600;
              border:1px solid var(--line);background:#0c1016;white-space:pre-wrap}
  .conclusion.pass{border-color:#1f6f3a;background:#0e2416;color:#5ddb8a}
  .conclusion.fail{border-color:#7a2b28;background:#250f0f;color:#ff9a95}
  .conclusion.run{border-color:#2a4a75;background:#0d1826;color:#79b8ff}
  .table-wrap{margin-top:10px;overflow-x:auto}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{border-bottom:1px solid var(--line);padding:7px 8px;text-align:left;vertical-align:top}
  th{color:var(--muted);font-weight:600;font-size:12px}
  td.res{font-weight:700;white-space:nowrap}
  td.res.pass{color:#5ddb8a}td.res.fail{color:#ff9a95}
  td.sec{white-space:nowrap;font-family:ui-monospace,Menlo,Consolas,monospace;color:var(--muted)}
  td.det pre{margin:0;white-space:pre-wrap;word-break:break-word;font-size:12px;color:#b9c4d0;
             font-family:ui-monospace,Menlo,Consolas,monospace;max-height:190px;overflow:auto}
  .log{background:#080b10;border:1px solid var(--line);border-radius:8px;padding:10px;
       height:330px;overflow:auto;margin:0;font:12px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
       white-space:pre-wrap;word-break:break-word}
  .log .ui{color:#79b8ff}
  .log .out{color:#c9d3de}
  .chk{font-size:12px;color:var(--muted);display:inline-flex;align-items:center;gap:4px}
  .modal{position:fixed;inset:0;background:rgba(0,0,0,.62);display:flex;align-items:center;justify-content:center;padding:18px}
  .modal.hidden,.hidden{display:none}
  .modal-box{background:var(--card);border:1px solid var(--line);border-radius:10px;width:100%;
             max-width:720px;padding:14px;display:flex;flex-direction:column;gap:9px;max-height:88vh}
  .modal-head{font-weight:600;display:flex;align-items:center;gap:8px}
  .modal-head .btn{margin-left:auto}
  .modal-path{display:flex;gap:7px;align-items:center;flex-wrap:wrap}
  .modal-path input{flex:1;min-width:180px}
  .ls-list{border:1px solid var(--line);border-radius:8px;overflow:auto;max-height:44vh;background:#0c1016}
  .ls-row{padding:6px 10px;cursor:pointer;border-bottom:1px solid #151b23;
          font:12px/1.4 ui-monospace,Menlo,Consolas,monospace;display:flex;gap:8px}
  .ls-row:hover{background:#1b2431}
  .ls-row .tag{color:var(--muted);font-size:11px;margin-left:auto}
  .modal-foot{display:flex;align-items:center;gap:10px}
  .modal-foot .btn.primary{margin-left:auto}
  .err{color:#ff9a95;font-size:12px}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>跨仓移动/碰撞一致性语料对齐</h1>
    <div class="sub">本地控制台 · 只监听 127.0.0.1 · 与 CI 走同一个 CLI（scripts/corpus_align.py）</div>
  </header>

  <section class="card">
    <div class="card-title">候选路径（点一下填入，只列真实存在的目录）</div>
    <div id="chips" class="chips"><span class="muted">探测中…</span></div>
  </section>

  <section class="card">
    <div class="card-title">仓库路径</div>
    <div class="field">
      <label>服务端仓库</label>
      <input id="serverDir" type="text" spellcheck="false" placeholder="/path/to/starve">
      <button class="btn" data-browse="server">浏览…</button>
    </div>
    <div class="field">
      <label>客户端仓库</label>
      <input id="clientDir" type="text" spellcheck="false" placeholder="/path/to/starve-godot">
      <button class="btn" data-browse="client">浏览…</button>
    </div>
    <div class="field small">
      <label>--regen 参数</label>
      <span class="muted">seed <input id="seed" type="number" value="11"></span>
      <span class="muted">count <input id="count" type="number" value="400"></span>
      <span class="muted">单步超时(秒) <input id="timeout" type="number" value="1800"></span>
    </div>
    <div class="actions">
      <button id="btnRun" class="btn primary">▶ 跑对齐</button>
      <button id="btnRegen" class="btn warn">⟳ 重新生成语料</button>
      <button id="btnStop" class="btn danger" disabled>■ 停止</button>
    </div>
    <div id="hint" class="hint"></div>
  </section>

  <section class="card">
    <div class="card-title">结论 <span id="statusPill" class="pill idle">空闲</span>
      <span class="right muted" id="cmdline"></span></div>
    <div id="conclusion" class="conclusion">尚未运行。选好两个仓库路径后点「跑对齐」。</div>
    <div id="reportWrap" class="table-wrap hidden">
      <table>
        <thead><tr><th style="width:44%">步骤</th><th style="width:70px">结果</th><th style="width:80px">耗时</th><th>详情</th></tr></thead>
        <tbody id="reportBody"></tbody>
      </table>
      <div class="muted" id="reportSource"></div>
    </div>
  </section>

  <section class="card">
    <div class="card-title">实时日志 <span class="muted" id="logCount"></span>
      <span class="right">
        <label class="chk"><input type="checkbox" id="autoScroll" checked>自动滚动</label>
        <button class="btn tiny" id="btnCopy">复制</button>
        <button class="btn tiny" id="btnClear">清空</button>
      </span>
    </div>
    <pre id="log" class="log"></pre>
  </section>
</div>

<div id="modal" class="modal hidden">
  <div class="modal-box">
    <div class="modal-head">选择目录 — <span id="modalTarget" class="muted"></span>
      <button class="btn tiny" id="modalClose">✕ 关闭</button></div>
    <div class="modal-path">
      <input id="lsPath" type="text" spellcheck="false" placeholder="目录路径，回车前往">
      <button class="btn" id="btnGo">前往</button>
      <button class="btn" id="btnUp">上一层</button>
      <button class="btn" id="btnHome">主目录</button>
    </div>
    <div id="roots" class="chips"></div>
    <div id="lsList" class="ls-list"></div>
    <div class="modal-foot">
      <span id="lsError" class="err"></span>
      <button class="btn primary" id="btnPick">选这个目录</button>
    </div>
  </div>
</div>

<script>
const S = {target:'server', ls:null, logNext:0, running:false, booted:false, finishedAt:null};
const $ = (id) => document.getElementById(id);

async function api(path, opts){
  try{
    const r = await fetch(path, opts);
    const j = await r.json().catch(() => null);
    if(j === null) return {ok:false, error:'响应不是 JSON（HTTP '+r.status+'）'};
    if(!r.ok && !j.error) j.error = 'HTTP ' + r.status;
    return j;
  }catch(e){ return {ok:false, error:'请求失败：'+e}; }
}
function esc(s){ return String(s==null?'':s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
function hint(msg, cls){ const h=$('hint'); h.className='hint'+(cls?' '+cls:''); h.textContent=msg||''; }
function setBusy(running){
  S.running = running;
  $('btnRun').disabled = running; $('btnRegen').disabled = running; $('btnStop').disabled = !running;
}

/* ---------- 候选路径 ---------- */
async function loadDetect(){
  const d = await api('/api/detect');
  const box = $('chips');
  if(!d.ok){ box.innerHTML = '<span class="err">'+esc(d.error||'探测失败')+'</span>'; return; }
  const list = (d.candidates||[]).filter(c => c.exists);
  box.innerHTML = '';
  if(!list.length){ box.innerHTML = '<span class="muted">没找到现成的候选目录，请用「浏览…」手动选。</span>'; }
  list.forEach(c => {
    const el = document.createElement('button');
    el.className = 'chip ' + c.role;
    const badge = c.role==='server' ? '服务端' : c.role==='client' ? '客户端' : c.role==='either' ? '两者皆像' : '普通目录';
    el.innerHTML = esc(c.label) + '<span class="badge">'+badge+'</span>';
    el.title = c.path + (c.has_movecorpus ? '' : '\n（缺 cmd/movecorpus，生成器可能还没实现）');
    el.onclick = () => {
      if(c.role === 'server' || (!c.has_client_tests && c.has_go_mod)) fill('server', c.path);
      else if(c.role === 'client') fill('client', c.path);
      else { if(!$('serverDir').value) fill('server', c.path); else fill('client', c.path); }
      hint('已填入：' + c.path, 'ok');
    };
    box.appendChild(el);
  });
  const st = d.settings || {};
  if(!S.booted){
    S.booted = true;
    if(st.server_dir) $('serverDir').value = st.server_dir;
    if(st.client_dir) $('clientDir').value = st.client_dir;
    if(st.seed) $('seed').value = st.seed;
    if(st.count) $('count').value = st.count;
    if(d.state_file) $('hint').textContent = '上次选择记忆在 ' + d.state_file;
  }
}
function fill(which, path){ $(which === 'server' ? 'serverDir' : 'clientDir').value = path; }

/* ---------- 目录浏览器 ---------- */
function openBrowser(target){
  S.target = target;
  $('modalTarget').textContent = target === 'server' ? '服务端仓库' : '客户端仓库';
  $('modal').classList.remove('hidden');
  const cur = $(target === 'server' ? 'serverDir' : 'clientDir').value.trim();
  ls(cur || '');
}
function closeBrowser(){ $('modal').classList.add('hidden'); }
async function ls(path){
  const d = await api('/api/ls?path=' + encodeURIComponent(path||''));
  S.ls = d;
  $('lsError').textContent = d.ok ? '' : (d.error || '读取失败');
  $('lsPath').value = d.path || path || '';
  const roots = $('roots'); roots.innerHTML = '';
  (d.roots||[]).forEach(r => {
    const b = document.createElement('button');
    b.className = 'chip'; b.textContent = r;
    b.onclick = () => ls(r); roots.appendChild(b);
  });
  const list = $('lsList'); list.innerHTML = '';
  if(!d.ok){
    const div = document.createElement('div');
    div.className = 'ls-row'; div.innerHTML = '<span class="err">'+esc(d.error||'读取失败')+'</span>';
    list.appendChild(div); return;
  }
  if(!d.dirs || !d.dirs.length){
    list.innerHTML = '<div class="ls-row"><span class="muted">（没有子目录）</span></div>';
  }
  (d.dirs||[]).forEach(sub => {
    const row = document.createElement('div');
    row.className = 'ls-row';
    const info = document.createElement('span');
    info.textContent = '📁 ' + sub.name;
    row.appendChild(info);
    const tag = document.createElement('span');
    tag.className = 'tag'; tag.textContent = '进入 →';
    row.appendChild(tag);
    row.onclick = () => ls(sub.path);
    list.appendChild(row);
  });
  const role = d.role === 'server' ? '看起来是服务端仓库' : d.role === 'client' ? '看起来是客户端仓库'
             : d.role === 'either' ? '两者皆像' : '普通目录';
  $('lsError').textContent = d.ok ? ('当前：' + role) : (d.error||'');
}
$('lsPath').addEventListener('keydown', e => { if(e.key === 'Enter') ls($('lsPath').value.trim()); });
$('btnGo').onclick = () => ls($('lsPath').value.trim());
$('btnUp').onclick = () => { if(S.ls && S.ls.parent) ls(S.ls.parent); };
$('btnHome').onclick = () => ls(S.ls ? S.ls.home : '');
$('modalClose').onclick = closeBrowser;
$('modal').addEventListener('click', e => { if(e.target.id === 'modal') closeBrowser(); });
$('btnPick').onclick = () => {
  if(!S.ls || !S.ls.path){ $('lsError').textContent = '还没有可选的目录'; return; }
  fill(S.target, S.ls.path);
  closeBrowser();
  hint('已填入：' + S.ls.path, 'ok');
};
document.querySelectorAll('[data-browse]').forEach(b => b.onclick = () => openBrowser(b.dataset.browse));

/* ---------- 运行 / 停止 ---------- */
function params(){
  return {
    server_dir: $('serverDir').value.trim(),
    client_dir: $('clientDir').value.trim(),
    seed: parseInt($('seed').value, 10) || 11,
    count: parseInt($('count').value, 10) || 400,
    timeout: parseInt($('timeout').value, 10) || 1800,
  };
}
async function startRun(mode){
  const p = params();
  if(!p.server_dir || !p.client_dir){ hint('请先填写服务端与客户端仓库路径（可用「浏览…」或上面的候选按钮）', 'err'); return; }
  hint('正在启动…');
  $('log').textContent = ''; S.logNext = 0; S.finishedAt = null;
  $('reportWrap').classList.add('hidden'); $('reportBody').innerHTML = '';
  $('conclusion').className = 'conclusion run';
  $('conclusion').textContent = mode === 'regen' ? '正在重新生成语料…' : '正在跑对齐…';
  const d = await api(mode === 'regen' ? '/api/regen' : '/api/run', {
    method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(p)
  });
  if(!d.ok){ hint(d.error || '启动失败', 'err'); $('conclusion').className='conclusion fail'; $('conclusion').textContent = '启动失败：' + (d.error||''); }
  else { hint('已启动', 'ok'); setBusy(true); }
  pollStatus();
}
$('btnRun').onclick = () => startRun('align');
$('btnRegen').onclick = () => startRun('regen');
$('btnStop').onclick = async () => {
  hint('正在停止…');
  const d = await api('/api/stop', {method:'POST', headers:{'Content-Type':'application/json'}, body:'{}'});
  hint(d.ok ? '已发送停止信号' : (d.error || '当前没有在跑的任务'), d.ok ? 'ok' : 'err');
  pollStatus();
};

/* ---------- 日志（增量轮询） ---------- */
async function pollLog(){
  const d = await api('/api/log?since=' + S.logNext);
  if(!d.ok || !d.lines || !d.lines.length){ if(typeof d.next === 'number') S.logNext = d.next; return; }
  const pre = $('log');
  d.lines.forEach(l => {
    const span = document.createElement('span');
    span.className = l.src === 'ui' ? 'ui' : 'out';
    span.textContent = l.text + '\n';
    pre.appendChild(span);
  });
  S.logNext = d.next;
  while(pre.childNodes.length > 6000) pre.removeChild(pre.firstChild);
  $('logCount').textContent = '(' + S.logNext + ' 行)';
  if($('autoScroll').checked) pre.scrollTop = pre.scrollHeight;
}
$('btnClear').onclick = () => { $('log').textContent = ''; };
$('btnCopy').onclick = async () => {
  const text = $('log').textContent;
  try{ await navigator.clipboard.writeText(text); hint('日志已复制到剪贴板', 'ok'); }
  catch(e){
    const ta = document.createElement('textarea');
    ta.value = text; document.body.appendChild(ta); ta.select();
    try{ document.execCommand('copy'); hint('日志已复制到剪贴板', 'ok'); }
    catch(e2){ hint('复制失败，请手动选中日志区域', 'err'); }
    document.body.removeChild(ta);
  }
};

/* ---------- 状态 / 报告 ---------- */
async function pollStatus(){
  const st = await api('/api/status');
  if(!st.ok && st.error){ return; }
  setBusy(!!st.running);
  $('cmdline').textContent = st.cmdline ? ('CLI: python3 scripts/corpus_align.py …') : '';
  const pill = $('statusPill'), con = $('conclusion');
  if(st.running){
    pill.className = 'pill run'; pill.textContent = '运行中';
    con.className = 'conclusion run';
    con.textContent = (st.mode === 'regen' ? '正在重新生成语料…' : '正在跑对齐…') +
      (st.elapsed ? ('　已用 ' + st.elapsed.toFixed(0) + ' 秒') : '');
    return;
  }
  if(st.finished_at && st.finished_at !== S.finishedAt){ S.finishedAt = st.finished_at; }
  if(st.stopped && !st.report){
    pill.className = 'pill idle'; pill.textContent = '已停止';
    con.className = 'conclusion'; con.textContent = '任务已被手动停止，未产生结论。';
    return;
  }
  if(st.report){
    renderReport(st);
  } else if(!st.finished_at){
    pill.className = 'pill idle'; pill.textContent = '空闲';
  }
}
function renderReport(st){
  const rep = st.report, pill = $('statusPill'), con = $('conclusion');
  const pass = !!rep.ok;
  pill.className = 'pill ' + (pass ? 'pass' : 'fail');
  pill.textContent = pass ? '通过' : '未通过';
  con.className = 'conclusion ' + (pass ? 'pass' : 'fail');
  const steps = rep.steps || [];
  const bad = steps.filter(s => !s.ok);
  let text = pass ? '✓ 两端语料对齐通过' : '✗ 未通过';
  if(!pass){
    if(bad.length) text += '：' + bad[0].name.split('（')[0] + ' 失败';
    if(st.exit_code !== null && st.exit_code !== undefined && st.exit_code !== 0) text += '（CLI 退出码 ' + st.exit_code + '）';
    text += '\n详见下方汇总表与日志——失败原因已原样透出。';
  }
  con.textContent = text;
  const body = $('reportBody'); body.innerHTML = '';
  steps.forEach(s => {
    const tr = document.createElement('tr');
    const det = document.createElement('pre');
    det.textContent = s.detail || '';
    const td = document.createElement('td');
    td.className = 'det'; td.appendChild(det);
    tr.innerHTML = '<td>' + esc(s.name) + '</td>' +
      '<td class="res ' + (s.ok ? 'pass' : 'fail') + '">' + (s.ok ? 'PASS' : 'FAIL') + '</td>' +
      '<td class="sec">' + (typeof s.seconds === 'number' ? s.seconds.toFixed(1) + 's' : '-') + '</td>';
    tr.appendChild(td);
    body.appendChild(tr);
  });
  if(!steps.length){
    body.innerHTML = '<tr><td colspan="4" class="muted">没有解析到分步结果，请直接看日志。</td></tr>';
  }
  $('reportSource').textContent = '报告来源：' + (st.report_source === 'json' ? '--json 结构化报告'
      : '从 CLI 日志重建（CLI 在环境检查阶段失败时不写 --json）') + '　日志 ' + (st.log_count||0) + ' 行';
  $('reportWrap').classList.remove('hidden');
}

/* ---------- 启动 ---------- */
async function boot(){
  await loadDetect();
  await pollStatus();
  setInterval(pollLog, 600);
  setInterval(pollStatus, 1000);
}
boot();
</script>
</body>
</html>
"""


def make_handler(runner: Runner, state: dict):
    class Handler(BaseHTTPRequestHandler):
        server_version = "StarveCorpusAlignUI/1.0"
        protocol_version = "HTTP/1.1"

        # ---------- helpers ----------
        def _send(self, body: bytes, status: int = 200, ctype: str = "application/json; charset=utf-8"):
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def _json(self, obj, status: int = 200):
            self._send(json.dumps(obj, ensure_ascii=False).encode("utf-8"), status)

        def _read_json(self) -> dict:
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                length = 0
            if length <= 0:
                return {}
            raw = self.rfile.read(length)
            try:
                data = json.loads(raw.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                return {}
            return data if isinstance(data, dict) else {}

        def log_message(self, fmt, *args):  # 精简访问日志
            sys.stderr.write("[ui] %s - %s\n" % (self.address_string(), fmt % args))

        # ---------- GET ----------
        def do_GET(self):
            parsed = urllib.parse.urlparse(self.path)
            route = parsed.path.rstrip("/") or "/"
            query = urllib.parse.parse_qs(parsed.query)
            if route == "/":
                self._send(INDEX_HTML.encode("utf-8"), 200, "text/html; charset=utf-8")
            elif route == "/api/detect":
                self._json({"ok": True, "candidates": detect_candidates(),
                            "settings": {k: state.get(k) for k in STATE_KEYS},
                            "state_file": str(STATE_FILE), "repo_root": str(REPO_ROOT)})
            elif route == "/api/ls":
                self._json(api_ls(query.get("path", [""])[0]))
            elif route == "/api/log":
                try:
                    since = int(query.get("since", ["0"])[0])
                except ValueError:
                    since = 0
                lines, nxt, oldest = runner.log_since(since)
                self._json({"ok": True, "lines": lines, "next": nxt,
                            "oldest": oldest, "running": runner.status(state)["running"]})
            elif route == "/api/status":
                self._json({"ok": True, **runner.status(state)})
            elif route == "/favicon.ico":
                self._send(b"", 204)
            else:
                self._json({"ok": False, "error": f"未知路由：{route}"}, 404)

        # ---------- POST ----------
        def do_POST(self):
            parsed = urllib.parse.urlparse(self.path)
            route = parsed.path.rstrip("/") or "/"
            body = self._read_json()
            if route in ("/api/run", "/api/regen"):
                def pick(key):
                    # 显式传了空串 = 用户就是没填（要报错）；键缺失才回落到上次记忆。
                    if key in body and body[key] is not None:
                        return str(body[key]).strip()
                    return str(state.get(key) or "").strip()

                server = pick("server_dir")
                client = pick("client_dir")
                if not server or not client:
                    self._json({"ok": False, "error": "请先填写服务端与客户端仓库路径"}, 400)
                    return
                try:
                    seed = int(body.get("seed") or state.get("seed") or DEFAULT_SEED)
                    count = int(body.get("count") or state.get("count") or DEFAULT_COUNT)
                    timeout = int(body.get("timeout") or DEFAULT_TIMEOUT)
                except (TypeError, ValueError):
                    self._json({"ok": False, "error": "seed/count/timeout 必须是整数"}, 400)
                    return
                server_p = str(Path(server).expanduser().resolve())
                client_p = str(Path(client).expanduser().resolve())
                mode = "regen" if route == "/api/regen" else "align"
                ok, err = runner.start(mode, server_p, client_p, seed, count, timeout, state)
                if not ok:
                    self._json({"ok": False, "error": err}, 409)
                else:
                    self._json({"ok": True, "mode": mode, "server_dir": server_p,
                                "client_dir": client_p,
                                "warnings": preflight(server_p, client_p)})
            elif route == "/api/stop":
                stopped = runner.stop()
                if stopped:
                    self._json({"ok": True})
                else:
                    self._json({"ok": False, "error": "当前没有在跑的任务"}, 409)
            else:
                self._json({"ok": False, "error": f"未知路由：{route}"}, 404)

    return Handler


# --------------------------------------------------------------------------- #
def _install_signal_handlers() -> None:
    def _handler(signum, frame):
        raise KeyboardInterrupt

    for name in ("SIGTERM", "SIGHUP"):
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            signal.signal(sig, _handler)
        except (ValueError, OSError):
            pass


def main(argv=None) -> int:
    # 输出重定向到文件/管道时 stdout 会变成块缓冲，导致"地址"这行迟迟不出现、
    # 甚至退出时丢掉。强制行缓冲，双击脚本和管道两种用法都能立刻看到。
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(line_buffering=True)
        except (AttributeError, ValueError, OSError):
            pass

    ap = argparse.ArgumentParser(
        description="跨仓移动/碰撞一致性语料对齐的本地图形控制台（标准库实现，仅监听 127.0.0.1）",
    )
    ap.add_argument("--port", type=int, default=0, help="监听端口（默认 0 = 自动挑一个空闲端口）")
    ap.add_argument("--no-open", action="store_true", help="不自动打开浏览器")
    ap.add_argument("--server-dir", default=None, help="预填服务端仓库路径")
    ap.add_argument("--client-dir", default=None, help="预填客户端仓库路径")
    args = ap.parse_args(argv)

    if not CLI.is_file():
        print(f"[ui] 错误：找不到 CLI：{CLI}", file=sys.stderr)
        return 2

    state = load_state()
    if args.server_dir:
        state["server_dir"] = str(Path(args.server_dir).expanduser().resolve())
    if args.client_dir:
        state["client_dir"] = str(Path(args.client_dir).expanduser().resolve())

    runner = Runner()
    _install_signal_handlers()
    try:
        httpd = ThreadingHTTPServer((HOST, args.port), make_handler(runner, state))
    except OSError as exc:
        print(f"[ui] 错误：无法绑定 {HOST}:{args.port} —— {exc}", file=sys.stderr)
        return 2

    port = httpd.server_address[1]
    url = f"http://{HOST}:{port}/"
    print("[ui] 跨端语料对齐本地控制台")
    print(f"[ui] 地址：{url}   （只监听 127.0.0.1，仅本机可用）")
    print(f"[ui] 客户端仓库：{REPO_ROOT}")
    print(f"[ui] 调用的 CLI：{CLI}")
    print(f"[ui] 记忆文件：{STATE_FILE}")
    print("[ui] Ctrl-C 退出（会一并杀掉正在跑的子进程）")

    if not args.no_open:
        threading.Timer(0.4, lambda: webbrowser.open(url)).start()

    try:
        httpd.serve_forever(poll_interval=0.3)
    except KeyboardInterrupt:
        print("\n[ui] 收到中断信号，正在退出…")
    finally:
        if runner.stop(join=True):
            print("[ui] 已终止正在运行的子进程")
        try:
            httpd.server_close()
        except OSError:
            pass
    print("[ui] 已退出。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
