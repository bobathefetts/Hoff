#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
root=$PWD
work="$root/.build"
source "$root/native/dependencies.sh"
archive="$downloads/sqlite-amalgamation-3540100.zip"
pin sha3-256 92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30 \
    https://www.sqlite.org/2026/sqlite-amalgamation-3540100.zip "$archive" "$reference/dotnet-native/sqlite-amalgamation.zip"
sqlite="$work/sqlite-amalgamation-3540100"
if [[ ! -f "$sqlite/sqlite3.c" ]]; then python3 -m zipfile -e "$archive" "$work"; fi
cd "$work"
printf '101 RCDATA "Application.exe"\n1 24 "../native/application.manifest"\n1 ICON "../native/reviewer.ico"\n' > application.rc
"$toolchain/x86_64-w64-mingw32-windres" application.rc -O coff -o application.res
if [[ ! -f sqlite.o || "$sqlite/sqlite3.c" -nt sqlite.o ]]; then
    "$toolchain/x86_64-w64-mingw32-clang" -O2 -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_THREADSAFE=1 \
        -c "$sqlite/sqlite3.c" -o sqlite.o
fi
codecs=${MCR_FFMPEG_SOURCE:-"$work/ffmpeg-9.0.2"}
if [[ ! -f "$codecs/libavformat/libavformat.a" ]]; then
    printf '%s\n' 'Build the static codecs with native/build-codecs.sh before linking.' >&2
    exit 1
fi
if [[ ! -f "$codecs/mcr-cli/libmcr-ffmpeg.a" ]]; then
    printf '%s\n' 'Rebuild the static codecs with native/build-codecs.sh to include playback support.' >&2
    exit 1
fi
dav1d="$work/dav1d-install"
if [[ ! -f "$dav1d/lib/libdav1d.a" ]]; then
    printf '%s\n' 'Build the AV1 decoder with native/build-av1.sh before linking.' >&2
    exit 1
fi
ai="${MCR_AI_WORK:-$work/ai}/cv-install"
if [[ ! -d "$ai/include/opencv2" && ! -d "$ai/include/opencv5/opencv2" ]]; then
    printf '%s\n' 'Build the static AI engine with native/build-ai.sh before linking.' >&2
    exit 1
fi
ai_header="$ai/include"
if [[ -d "$ai/include/opencv5/opencv2" ]]; then ai_header="$ai/include/opencv5"; fi
mapfile -t ai_libraries < <(find "$ai" -name '*.a' -type f | sort)
"$toolchain/x86_64-w64-mingw32-clang++" -O2 -std=c++17 -I"$ai_header" -c "$root/native/draft_model.cpp" -o draft_model.o
gemma=${MCR_GEMMA_WORK:-$work/gemma}
llama="$gemma/llama.cpp-b12141"
vulkan="$gemma/win/ggml/src/ggml-vulkan/ggml-vulkan.a"
if [[ ! -f "$gemma/win/tools/mtmd/libmtmd.a" || ! -f "$vulkan" ]]; then
    printf '%s\n' 'Build the static Gemma engine with native/build-gemma.sh before linking.' >&2
    exit 1
fi
"$toolchain/x86_64-w64-mingw32-clang++" -O2 -std=c++17 -I"$llama/include" -I"$llama/ggml/include" -I"$llama/tools/mtmd" \
    -c "$root/native/gemma_draft.cpp" -o gemma_draft.o
"$toolchain/x86_64-w64-mingw32-clang++" -O2 -std=c++17 -I"$gemma/Vulkan-Headers-vulkan-sdk-1.4.350.0/include" \
    -c "$root/native/gemma_vulkan_win.cpp" -o gemma_vulkan_win.o
gemma_libraries=("$gemma/win/tools/mtmd/libmtmd.a" "$gemma/win/src/libllama.a" "$gemma/win/ggml/src/"{ggml,ggml-cpu,ggml-base}.a "$gemma/win/vendor/hash/libvendor-hash.a" "$vulkan")
# Compile FFmpeg's upstream dav1d adapter against the pinned FFmpeg tree. The
# gallery decoder selects this software adapter explicitly for AV1 streams.
"$toolchain/x86_64-w64-mingw32-clang" -O2 -DHAVE_AV_CONFIG_H -I"$codecs" -I"$dav1d/include" \
    -c "$codecs/libavcodec/libdav1d.c" -o ffmpeg-dav1d.o
"$toolchain/x86_64-w64-mingw32-clang" -O2 -static -municode -mwindows -I"$codecs" \
    -Wl,--dynamicbase,--nxcompat,--high-entropy-va,--dependent-load-flag=0x800 \
    "$root/native/host.c" "$root/native/decode.c" "$root/native/playback.c" "$root/native/exports.def" application.res sqlite.o ffmpeg-dav1d.o draft_model.o gemma_draft.o gemma_vulkan_win.o \
    -L"$codecs/libavformat" -L"$codecs/libavcodec" -L"$codecs/libswscale" -L"$codecs/libavutil" -L"$codecs/libavfilter" -L"$codecs/libswresample" \
    -Wl,--start-group "$codecs/mcr-cli/libmcr-ffmpeg.a" -lavfilter -lavformat -lavcodec -lswscale -lswresample -lavutil -Wl,--end-group \
    -Wl,--start-group "${ai_libraries[@]}" "${gemma_libraries[@]}" -Wl,--end-group -lc++ -lc++abi -lwinpthread \
    "$dav1d/lib/libdav1d.a" -lole32 -loleaut32 -lbcrypt -lpsapi -lshell32 -luser32 \
    -o "$work/Reviewer-linked.exe"
