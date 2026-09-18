@echo off
rem 双击即跑：跨仓语料对齐本地控制台（Windows）
rem 优先用官方 py launcher（能挑到 Python 3），否则退回 PATH 里的 python。
setlocal
cd /d "%~dp0.."

set "PY="
where py >nul 2>nul && set "PY=py -3"
if not defined PY (
    where python >nul 2>nul && set "PY=python"
)
if not defined PY (
    echo 找不到 Python 3。请先安装 Python 3.8+（https://www.python.org/downloads/）后重试。
    echo 安装时记得勾选 "Add python.exe to PATH"。
    pause
    exit /b 2
)

echo 使用解释器：%PY%
%PY% -u scripts\corpus_align_ui.py %*
set "RC=%ERRORLEVEL%"

echo.
echo 进程已退出（退出码 %RC%）。
pause
exit /b %RC%
