#!/bin/sh
# Builds the DirectShow camera Firefox uses. Requires the MinGW-w64 posix or
# win32 cross compiler (x86_64-w64-mingw32-g++).
set -eu
cd "$(dirname "$0")"
CXX="${CXX:-x86_64-w64-mingw32-g++}"
"$CXX" -shared -O2 -static -static-libgcc -static-libstdc++ \
  -std=c++17 -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 \
  -fno-exceptions -fno-rtti \
  -o MeetCamDShow.dll MeetCamDShow.cpp MeetCamDShow.def \
  -lole32 -loleaut32 -lstrmiids -luuid -ladvapi32 -Wl,--kill-at
"$CXX" -O2 -static -static-libgcc -static-libstdc++ \
  -std=c++17 -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 \
  -municode -o MeetCamDShowTest.exe MeetCamDShowTest.cpp \
  -lole32 -loleaut32 -lstrmiids -luuid
