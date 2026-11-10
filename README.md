# MeetCam

녹화해 둔 영상을 Google Meet 카메라로 보냅니다. 영상이 끝나면 처음부터 다시 재생합니다. 마이크는 바꾸지 않습니다.

송출 크기는 1280x720, 30fps입니다. 원본 비율은 유지하고 빈 곳은 검은 여백으로 채웁니다.

## 지원 환경

| 항목 | 지원 |
| --- | --- |
| 운영체제 | Windows 11 64비트 (빌드 22621 이상) |
| 브라우저 | Google Chrome, Microsoft Edge, Firefox |
| 런타임 | .NET 9 |

Windows 10, macOS, Linux에서는 동작하지 않습니다. 가상 카메라 API가 Windows 11에만 있습니다.

Chrome과 Edge는 Windows 가상 카메라로 MeetCam을 봅니다. 목록 이름은 `MeetCam (Windows 가상 카메라)`입니다. Firefox는 DirectShow 카메라만 사용하며, 목록 이름은 `MeetCam`입니다. 두 이름 모두 이 프로그램이 만든 카메라입니다. `HD Camera`처럼 실제 카메라는 고르지 않습니다.

## 준비

1. [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)를 설치합니다.
2. Firefox에서도 쓰려면 DirectShow DLL을 먼저 만듭니다. Windows에 MinGW-w64(`x86_64-w64-mingw32-g++`)가 있어야 합니다.

```bat
native\MeetCamDShow\build.sh
```

DLL이 없으면 Chrome과 Edge용 카메라만 등록됩니다.

3. 관리자 권한으로 설치합니다. 설치 위치는 `C:\Users\Public\MeetCam`입니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

등록 단계에서 권한 요청이 거절되면, 프로그램을 실행한 뒤 송출을 시작할 때 다시 허용하면 됩니다. 등록은 이 PC에서 한 번이면 됩니다.

## 사용 방법

1. `MeetCam.cmd` 또는 `C:\Users\Public\MeetCam\MeetCam.exe`를 실행합니다.
2. `찾기`로 영상을 고르거나, 파일을 창에 끌어 놓습니다. mp4, mkv, avi, mov, webm을 열 수 있습니다.
3. `송출 시작`을 누릅니다. 처음 한 번은 관리자 권한 창이 나오면 허용합니다.
4. Google Meet 설정에서 카메라를 MeetCam으로 바꿉니다.
5. 송출하는 동안 이 창을 닫지 않습니다. 영상이 끝나면 자동으로 반복됩니다.
6. 멈추려면 `중지`를 누릅니다.

Meet에 영상을 보이게 하려면 앱이 송출 중이어야 합니다. 앱을 닫아도 카메라 이름은 목록에 남을 수 있지만, 그때는 새 영상이 나가지 않습니다.
