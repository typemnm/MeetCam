@echo off
set "EXE=C:\Users\Public\MeetCam\MeetCam.exe"
if not exist "%EXE%" (
  echo MeetCam이 아직 설치되지 않았습니다.
  echo PowerShell에서 scripts\publish.ps1 을 실행한 뒤 이 파일을 다시 실행하세요.
  exit /b 1
)
start "" "%EXE%" %*
