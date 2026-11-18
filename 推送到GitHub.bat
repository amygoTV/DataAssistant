@echo off
chcp 65001 >nul
REM ============================================================
REM  DataAssistant sources -> GitHub public repo
REM  BEFORE RUNNING: create an EMPTY public repo at https://github.com/new
REM  Name it DataAssistant. Do NOT check README/license/.gitignore.
REM  Then double-click this .bat to push.
REM ============================================================
cd /d "%~dp0"
set REPO=https://github.com/amygoTV/DataAssistant.git

echo === Branch: master ===
git branch --show-current

echo === Add remote ===
git remote remove origin 2>nul
git remote add origin %REPO%

echo === Push ===
git push -u origin master
if errorlevel 1 (
  echo.
  echo [FAILED] If 404/Repository not found: create the empty repo on GitHub first.
  echo [FAILED] If auth failed: check GitHub credential in Windows Credential Manager.
  pause
  exit /b 1
)
echo.
echo === Push OK ===
echo Repo: %REPO%
pause
