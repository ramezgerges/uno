#!/bin/bash

set -e

rm -rf build
cd UnoNativeMac
chmod +x getSkiaSharpDylib.sh
./getSkiaSharpDylib.sh
cd ..
xcodebuild $@
mkdir -p ../runtimes/osx/native
# Copy from whichever configuration was built (Release or Debug)
cp -R build/Release/libUnoNativeMac.* ../runtimes/osx/native 2>/dev/null || \
cp -R build/Debug/libUnoNativeMac.* ../runtimes/osx/native 2>/dev/null || true
