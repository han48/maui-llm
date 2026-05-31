#!/bin/bash
# =============================================================================
# Build native libraries for Qwen3Maui (llama.cpp + mtmd)
# Builds for: macOS Catalyst, iOS, Android (arm64)
#
# This script builds ALL required native libraries:
#   - libggml-base, libggml, libggml-cpu, libggml-metal (macOS/iOS)
#   - libllama
#   - libmtmd (multimodal/vision support)
#
# Prerequisites:
#   - macOS with Xcode + Command Line Tools (xcode-select --install)
#   - CMake >= 3.21 (brew install cmake)
#   - For Android: Android NDK (set ANDROID_NDK_HOME)
#
# Usage:
#   ./scripts/build-native-libs.sh              # Build all platforms
#   ./scripts/build-native-libs.sh macos        # macOS Catalyst only
#   ./scripts/build-native-libs.sh ios          # iOS only
#   ./scripts/build-native-libs.sh android      # Android only
#
# Environment variables:
#   LLAMA_CPP_DIR     - Path to llama.cpp source (default: ../llama.cpp)
#   ANDROID_NDK_HOME  - Path to Android NDK
#   JOBS              - Number of parallel build jobs (default: CPU count)
# =============================================================================

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
LLAMA_CPP_DIR="${LLAMA_CPP_DIR:-$PROJECT_DIR/../llama.cpp}"
# Android NDK path - auto-detect latest installed version
if [ -z "$ANDROID_NDK_HOME" ]; then
    NDK_BASE="$HOME/Library/Android/sdk/ndk"
    if [ -d "$NDK_BASE" ]; then
        ANDROID_NDK_HOME=$(ls -d "$NDK_BASE"/*/ 2>/dev/null | sort -V | tail -1 | sed 's:/$::')
    fi
    ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-$NDK_BASE/27.2.12479018}"
fi
JOBS="${JOBS:-$(sysctl -n hw.ncpu 2>/dev/null || echo 8)}"

log_info()  { echo -e "${CYAN}[INFO]${NC} $1"; }
log_ok()    { echo -e "${GREEN}[OK]${NC}   $1"; }
log_warn()  { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERR]${NC}  $1"; }

# =============================================================================
echo ""
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║       Qwen3Maui - Native Libraries Build Script            ║"
echo "║       (llama.cpp + libmtmd for vision support)             ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo ""
log_info "llama.cpp source: $LLAMA_CPP_DIR"
log_info "Project dir:      $PROJECT_DIR"
log_info "Parallel jobs:    $JOBS"
echo ""

# =============================================================================
# Check prerequisites
# =============================================================================
check_prerequisites() {
    local missing=0

    if ! command -v cmake &>/dev/null; then
        log_error "CMake not found. Install with: brew install cmake"
        missing=1
    fi

    if ! command -v git &>/dev/null; then
        log_error "Git not found."
        missing=1
    fi

    if [ "$missing" -eq 1 ]; then
        exit 1
    fi

    log_ok "Prerequisites OK (cmake, git)"
}

# =============================================================================
# Clone/update llama.cpp
# =============================================================================
prepare_source() {
    if [ ! -d "$LLAMA_CPP_DIR" ]; then
        log_info "Cloning llama.cpp..."
        git clone --depth 1 https://github.com/ggml-org/llama.cpp "$LLAMA_CPP_DIR"
        log_ok "Cloned llama.cpp"
    else
        log_info "llama.cpp found at $LLAMA_CPP_DIR"
        log_info "Pulling latest changes..."
        (cd "$LLAMA_CPP_DIR" && git pull --ff-only 2>/dev/null || true)
    fi
}

# =============================================================================
# macOS Catalyst (arm64) - Shared libraries (.dylib)
# =============================================================================
build_macos() {
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "  Building for macOS Catalyst (arm64)"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    local BUILD_DIR="$LLAMA_CPP_DIR/build-macos-catalyst"
    local OUTPUT_DIR="$PROJECT_DIR/Platforms/MacCatalyst/libs"

    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR" "$OUTPUT_DIR"

    log_info "Configuring CMake..."
    cmake -B "$BUILD_DIR" -S "$LLAMA_CPP_DIR" \
        -DCMAKE_OSX_ARCHITECTURES="arm64" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="15.0" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON \
        -DGGML_METAL=ON \
        -DGGML_BLAS=ON \
        -DGGML_RPC=ON \
        -DLLAMA_BUILD_TESTS=OFF \
        -DLLAMA_BUILD_EXAMPLES=OFF \
        -DLLAMA_BUILD_SERVER=OFF \
        2>&1 | tail -5

    log_info "Building (this may take a few minutes)..."
    cmake --build "$BUILD_DIR" --config Release -j "$JOBS" 2>&1 | tail -3

    # Copy all shared libraries
    log_info "Copying libraries to $OUTPUT_DIR..."
    local count=0

    # Find and copy all .dylib files
    for lib in $(find "$BUILD_DIR" -name "*.dylib" -not -path "*/CMakeFiles/*" | sort -u); do
        local basename=$(basename "$lib")
        # Skip versioned duplicates, keep the unversioned symlink target
        if [[ "$basename" == *".0."* ]] || [[ "$basename" == *".0.dylib" ]]; then
            continue
        fi
        cp "$lib" "$OUTPUT_DIR/$basename" 2>/dev/null || true
        count=$((count + 1))
    done

    # Ensure key libraries exist
    local required_libs=("libggml-base.dylib" "libggml.dylib" "libggml-cpu.dylib" "libllama.dylib" "libmtmd.dylib")
    for lib in "${required_libs[@]}"; do
        if [ -f "$OUTPUT_DIR/$lib" ]; then
            log_ok "  $lib"
        else
            # Try to find versioned variant
            local found=$(find "$BUILD_DIR" -name "${lib%.dylib}*dylib" | head -1)
            if [ -n "$found" ]; then
                cp "$found" "$OUTPUT_DIR/$lib"
                log_ok "  $lib (from versioned)"
            else
                log_warn "  $lib NOT FOUND"
            fi
        fi
    done

    log_ok "macOS Catalyst build complete ($count libraries)"

    # Fix @rpath to @loader_path so libraries find each other in the same directory
    log_info "Fixing dylib install names (@rpath -> @loader_path)..."
    for dylib in "$OUTPUT_DIR"/*.dylib; do
        install_name_tool -id "@loader_path/$(basename "$dylib")" "$dylib" 2>/dev/null || true
        for dep in libggml-base libggml libggml-cpu libggml-metal libggml-blas libggml-rpc libllama libmtmd; do
            install_name_tool -change "@rpath/$dep.0.dylib" "@loader_path/$dep.0.dylib" "$dylib" 2>/dev/null || true
            install_name_tool -change "@rpath/$dep.dylib" "@loader_path/$dep.dylib" "$dylib" 2>/dev/null || true
        done
    done
    log_ok "Install names fixed"
}

# =============================================================================
# iOS (arm64) - Static libraries (.a)
# =============================================================================
build_ios() {
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "  Building for iOS (arm64)"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    local BUILD_DIR="$LLAMA_CPP_DIR/build-ios-arm64"
    local OUTPUT_DIR="$PROJECT_DIR/Platforms/iOS/Frameworks"

    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR" "$OUTPUT_DIR"

    log_info "Configuring CMake for iOS..."
    cmake -B "$BUILD_DIR" -S "$LLAMA_CPP_DIR" \
        -DCMAKE_SYSTEM_NAME=iOS \
        -DCMAKE_OSX_ARCHITECTURES="arm64" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="15.0" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF \
        -DGGML_METAL=ON \
        -DGGML_BLAS=OFF \
        -DGGML_RPC=OFF \
        -DLLAMA_BUILD_TESTS=OFF \
        -DLLAMA_BUILD_EXAMPLES=OFF \
        -DLLAMA_BUILD_SERVER=OFF \
        2>&1 | tail -5

    log_info "Building (this may take a few minutes)..."
    cmake --build "$BUILD_DIR" --config Release -j "$JOBS" 2>&1 | tail -3

    # Copy static libraries
    log_info "Copying static libraries to $OUTPUT_DIR..."

    local required_libs=("libggml-base.a" "libggml.a" "libggml-cpu.a" "libggml-metal.a" "libllama.a" "libmtmd.a")
    for lib in "${required_libs[@]}"; do
        local found=$(find "$BUILD_DIR" -name "$lib" | head -1)
        if [ -n "$found" ]; then
            cp "$found" "$OUTPUT_DIR/$lib"
            log_ok "  $lib"
        else
            log_warn "  $lib NOT FOUND"
        fi
    done

    # Copy Metal shader
    local metal_lib=$(find "$BUILD_DIR" -name "default.metallib" | head -1)
    if [ -n "$metal_lib" ]; then
        cp "$metal_lib" "$OUTPUT_DIR/"
        log_ok "  default.metallib"
    fi

    # Copy headers (needed for static linking)
    cp "$LLAMA_CPP_DIR/tools/mtmd/mtmd.h" "$OUTPUT_DIR/" 2>/dev/null || true
    cp "$LLAMA_CPP_DIR/include/llama.h" "$OUTPUT_DIR/" 2>/dev/null || true

    log_ok "iOS build complete"
    echo ""
    log_info "NOTE: For iOS, create a fat static library or xcframework:"
    echo "  libtool -static -o $OUTPUT_DIR/libllama-all.a \\"
    echo "    $OUTPUT_DIR/libggml-base.a \\"
    echo "    $OUTPUT_DIR/libggml.a \\"
    echo "    $OUTPUT_DIR/libggml-cpu.a \\"
    echo "    $OUTPUT_DIR/libggml-metal.a \\"
    echo "    $OUTPUT_DIR/libllama.a \\"
    echo "    $OUTPUT_DIR/libmtmd.a"
}

# =============================================================================
# Android (arm64-v8a) - Shared libraries (.so)
# =============================================================================
build_android() {
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "  Building for Android (arm64-v8a)"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    # Check NDK
    if [ ! -d "$ANDROID_NDK_HOME" ]; then
        log_error "Android NDK not found at: $ANDROID_NDK_HOME"
        log_error "Set ANDROID_NDK_HOME to your NDK installation path."
        log_info "Example: export ANDROID_NDK_HOME=\$HOME/Library/Android/sdk/ndk/27.0.12077973"
        log_info "Install NDK via Android Studio > SDK Manager > SDK Tools > NDK"
        return 1
    fi

    local TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
    if [ ! -f "$TOOLCHAIN" ]; then
        log_error "Android CMake toolchain not found at: $TOOLCHAIN"
        return 1
    fi

    local BUILD_DIR="$LLAMA_CPP_DIR/build-android-arm64"
    local OUTPUT_DIR="$PROJECT_DIR/Platforms/Android/libs/arm64-v8a"

    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR" "$OUTPUT_DIR"

    log_info "Configuring CMake for Android arm64-v8a..."
    cmake -B "$BUILD_DIR" -S "$LLAMA_CPP_DIR" \
        -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
        -DANDROID_ABI=arm64-v8a \
        -DANDROID_PLATFORM=android-24 \
        -DANDROID_STL=c++_shared \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON \
        -DGGML_METAL=OFF \
        -DGGML_BLAS=OFF \
        -DGGML_RPC=OFF \
        -DLLAMA_BUILD_TESTS=OFF \
        -DLLAMA_BUILD_EXAMPLES=OFF \
        -DLLAMA_BUILD_SERVER=OFF \
        2>&1 | tail -5

    log_info "Building (this may take a few minutes)..."
    cmake --build "$BUILD_DIR" --config Release -j "$JOBS" 2>&1 | tail -3

    # Copy shared libraries
    log_info "Copying libraries to $OUTPUT_DIR..."

    local required_libs=("libggml-base.so" "libggml.so" "libggml-cpu.so" "libllama.so" "libmtmd.so")
    for lib in "${required_libs[@]}"; do
        local found=$(find "$BUILD_DIR" -name "$lib" -not -path "*/CMakeFiles/*" | head -1)
        if [ -n "$found" ]; then
            cp "$found" "$OUTPUT_DIR/$lib"
            log_ok "  $lib ($(du -h "$found" | cut -f1))"
        else
            log_warn "  $lib NOT FOUND"
        fi
    done

    # Copy libc++_shared.so (required by Android NDK when using c++_shared STL)
    local cxx_shared="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/sysroot/usr/lib/aarch64-linux-android/libc++_shared.so"
    if [ -f "$cxx_shared" ]; then
        cp "$cxx_shared" "$OUTPUT_DIR/"
        log_ok "  libc++_shared.so"
    fi

    log_ok "Android build complete"
}

# =============================================================================
# Summary
# =============================================================================
print_summary() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║                    Build Summary                           ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""

    # macOS
    local mac_dir="$PROJECT_DIR/Platforms/MacCatalyst/libs"
    if [ -f "$mac_dir/libmtmd.dylib" ]; then
        log_ok "macOS:   $mac_dir/libmtmd.dylib"
    else
        log_warn "macOS:   libmtmd.dylib not found"
    fi

    # iOS
    local ios_dir="$PROJECT_DIR/Platforms/iOS/Frameworks"
    if [ -f "$ios_dir/libmtmd.a" ]; then
        log_ok "iOS:     $ios_dir/libmtmd.a"
    else
        log_warn "iOS:     libmtmd.a not found"
    fi

    # Android
    local android_dir="$PROJECT_DIR/Platforms/Android/libs/arm64-v8a"
    if [ -f "$android_dir/libmtmd.so" ]; then
        log_ok "Android: $android_dir/libmtmd.so"
    else
        log_warn "Android: libmtmd.so not found"
    fi

    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "  Next steps:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "  1. Rebuild the MAUI project:"
    echo "     dotnet build -f net9.0-maccatalyst"
    echo "     dotnet build -f net9.0-android"
    echo ""
    echo "  2. Run the app and select a VL model (VL-2B or VL-4B)"
    echo ""
    echo "  3. The mmproj file will be downloaded automatically"
    echo "     on first use of a vision model."
    echo ""
    echo "  4. Attach an image and ask a question!"
    echo ""
}

# =============================================================================
# Main
# =============================================================================
check_prerequisites
prepare_source

TARGET="${1:-all}"

case "$TARGET" in
    macos|mac|catalyst)
        build_macos
        ;;
    ios|iphone)
        build_ios
        ;;
    android)
        build_android
        ;;
    all)
        build_macos
        build_ios
        build_android
        ;;
    *)
        echo "Usage: $0 [macos|ios|android|all]"
        echo ""
        echo "Options:"
        echo "  macos    Build for macOS Catalyst (arm64) - .dylib"
        echo "  ios      Build for iOS (arm64) - .a static"
        echo "  android  Build for Android (arm64-v8a) - .so"
        echo "  all      Build for all platforms (default)"
        echo ""
        echo "Environment variables:"
        echo "  LLAMA_CPP_DIR     Path to llama.cpp source (default: ../llama.cpp)"
        echo "  ANDROID_NDK_HOME  Path to Android NDK"
        echo "  JOBS              Parallel build jobs (default: CPU count)"
        exit 1
        ;;
esac

print_summary
