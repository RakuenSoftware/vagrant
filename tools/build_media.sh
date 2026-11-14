#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# Compile the packaged media tools from the verified, unmodified release sources.
set -eo pipefail
target="${1:?desktop target required}"
cd "${2:?build directory required}"
work="$(pwd)"
cd "${3:?output directory required}"
output="$(pwd)"
if [[ "$target" == windows-x64 ]]; then
    export PATH="/mingw64/bin:/usr/bin:$PATH"
fi
export LC_ALL=C
architectures=x86_64
[[ "$target" != macos-universal ]] || architectures="x86_64 arm64"
for arch in $architectures; do
    prefix="$work/$arch/prefix"
    mkdir -p "$work/$arch"
    cp -R "$work/sources" "$work/$arch/src"
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    export PKG_CONFIG_PATH="$PKG_CONFIG_LIBDIR"
    export CFLAGS="-O2"
    export CPPFLAGS="-I$prefix/include"
    export LDFLAGS="-L$prefix/lib"
    export CC=cc AR=ar RANLIB=ranlib
    host=()
    ffhost=()
    if [[ "$target" == macos-universal ]]; then
        export CC="clang -arch $arch"
        export CFLAGS="-O2 -arch $arch -mmacosx-version-min=11.0"
        export LDFLAGS="$LDFLAGS -arch $arch -mmacosx-version-min=11.0"
        host=("--host=${arch/arm64/aarch64}-apple-darwin$(uname -r)")
        ffhost=(--enable-cross-compile --target-os=darwin "--arch=$arch" --cc=clang)
    elif [[ "$target" == windows-x64 ]]; then
        export CC=gcc
        host=(--host=x86_64-w64-mingw32)
        ffhost=(--target-os=mingw32 --arch=x86_64 --cc=gcc)
        export LDFLAGS="$LDFLAGS -static"
    fi
    for package in libogg-1.3.6 libvorbis-1.3.7 libtheora-1.2.0; do
        cd "$work/$arch/src/$package"
        options=()
        [[ "$package" != libtheora-* ]] || options=(--disable-examples --disable-doc --disable-asm)
        ./configure "--prefix=$prefix" --disable-shared --enable-static --with-pic "${host[@]}" "${options[@]}"
        # Older Vorbis configure scripts replace CFLAGS with obsolete Darwin flags.
        # Keep the explicit target flags without modifying the upstream sources.
        make -j4 CFLAGS="$CFLAGS"
        make install CFLAGS="$CFLAGS"
    done
    cd "$work/$arch/src/ffmpeg-9.0.2"
    ./configure "--prefix=$prefix" "${ffhost[@]}" \
        --disable-autodetect --disable-everything --disable-doc --disable-debug \
        --disable-shared --enable-static --disable-x86asm \
        --disable-gpl --disable-nonfree --disable-network \
        --enable-ffmpeg --enable-ffprobe --enable-avfilter --enable-swscale --enable-swresample \
        --enable-libtheora --enable-libvorbis --pkg-config-flags=--static \
        "--extra-cflags=$CFLAGS $CPPFLAGS" "--extra-ldflags=$LDFLAGS" \
        --enable-protocol=file \
        --enable-demuxer=psxstr,ogg,wav --enable-indev=lavfi \
        --enable-decoder=mdec,adpcm_xa,pcm_s16le,rawvideo,theora,vorbis \
        --enable-encoder=libtheora,libvorbis,pcm_s16le \
        --enable-muxer=ogg,wav,null \
        --enable-filter=aresample,format,scale,anull,null,color,sine
    make -j4
    make install
    # Retain the exact configuration used by each architecture in the binary package.
    cp config.h "$prefix/config.h"
done
for tool in ffmpeg ffprobe; do
    if [[ "$target" == macos-universal ]]; then
        lipo -create "$work/x86_64/prefix/bin/$tool" "$work/arm64/prefix/bin/$tool" -output "$output/$tool"
        codesign --force --sign - "$output/$tool"
    elif [[ "$target" == windows-x64 ]]; then
        cp "$work/x86_64/prefix/bin/$tool.exe" "$output/$tool.exe"
    else
        cp "$work/x86_64/prefix/bin/$tool" "$output/$tool"
    fi
done
