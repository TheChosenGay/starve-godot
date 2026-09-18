# 跨端语料对齐 · 本地图形控制台

跑「跨仓移动/碰撞一致性语料对齐」的本地小工具：**打开 → 选两个仓库路径 → 点一下就跑**。
不需要装任何第三方包，只用 Python 标准库。

> 本机 `python3` 是 Homebrew 版，`_tkinter` 不可用（`import tkinter` 报 `No module named '_tkinter'`），
> 所以这里不用 Tkinter，而是起一个**只监听 `127.0.0.1`** 的本地 web 服务，用系统浏览器当界面。

## 怎么打开

| 方式 | 命令 |
| --- | --- |
| macOS 双击 | `scripts/corpus-align.command`（已在仓库里 `chmod +x`） |
| Windows 双击 | `scripts/corpus-align.bat` |
| 命令行 / Makefile | `make corpus-align-ui` 或 `python3 scripts/corpus_align_ui.py` |

启动后会打印 `http://127.0.0.1:<port>/` 并自动打开浏览器。关闭：终端里 `Ctrl-C`（会顺手杀掉正在跑的子进程）。

## 页面怎么用

1. **候选路径**：顶部自动列出**真实存在**的目录（`./`、`../starve`、`~/starve`、`~/starve-godot`、CI 的 `.contract-server` …），
   带「服务端 / 客户端」标签，点一下直接填入对应输入框。
2. **浏览…**：打开内置目录浏览器（浏览器拿不到本地文件系统，所以由本地服务提供列表接口）：
   进子目录 / 「上一层」/「主目录」/ 直接输路径回车，最后点「选这个目录」。
3. **跑对齐**：调用 `scripts/corpus_align.py --server-dir … --client-dir …`。
4. **重新生成语料**：等价 `--regen`，用旁边的 `seed` / `count` 输入框。
5. **停止**：终止当前子进程（含它拉起的 `go` / `dotnet` 子进程树）。
6. **实时日志**：stdout+stderr 逐行追加、自动滚动、可复制、可清空。
7. **结论**：结束后渲染每步 `PASS/FAIL + 耗时 + 详情` 的汇总表，以及一个醒目的大结论（通过 / 未通过）。

路径选择记在 `~/.starve-corpus-align.json`（`{server_dir, client_dir, seed, count}`），下次启动自动填入。

## 它和 CI 的关系

**同一套逻辑、同一个入口。** 本 UI 不重写任何对齐逻辑，只是 `subprocess` 调用
`scripts/corpus_align.py`，和 `make corpus-align`、CI 里跑的是同一个脚本、同一个 `--json` 报告格式。
所以「UI 里通过」和「CI 里通过」含义完全一致；UI 里看到的失败原因，就是 CI 里会看到的失败原因。

## 常用参数

```
python3 scripts/corpus_align_ui.py --port 8765     # 指定端口（默认 0 = 自动挑空闲端口）
python3 scripts/corpus_align_ui.py --no-open       # 不自动打开浏览器
python3 scripts/corpus_align_ui.py --server-dir ../starve --client-dir .
```

## 一句安全说明

这个本地服务**只监听 `127.0.0.1`**，仅用于本机开发；它不对外提供任何服务，也不要改成监听 `0.0.0.0`
（目录浏览接口能列出本机目录）。
