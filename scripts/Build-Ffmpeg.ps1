[CmdletBinding()]
param(
    [string]$Output = 'artifacts/local/m3b/vendor',
    [string]$MsysRoot = 'C:/msys64',
    [string]$Gpg = 'C:/Program Files/Git/usr/bin/gpg.exe',
    [ValidateRange(1, 32)][int]$Jobs = 8
)
$ErrorActionPreference = 'Stop'
$root = (New-Item -ItemType Directory -Force $Output).FullName
$downloads = (New-Item -ItemType Directory -Force (Join-Path $root 'downloads')).FullName
$sysroot = (New-Item -ItemType Directory -Force (Join-Path $root 'sysroot')).FullName
$bash = Join-Path $MsysRoot 'usr/bin/bash.exe'
if (-not (Test-Path $bash) -or -not (Test-Path (Join-Path $MsysRoot 'ucrt64/bin/gcc.exe'))) {
    throw 'Install the MSYS2 UCRT64 GCC toolchain, or pass its installation directory.'
}
$inputs = @(
    @('https://www.ffmpeg.org/releases/ffmpeg-7.1.4.tar.xz','71F4AAC3573ED9060489CB62526A6C7DDA815AE10993789611ACD7BE9FA9FBF4'),
    @('https://www.ffmpeg.org/releases/ffmpeg-7.1.4.tar.xz.asc','97FF724B551ABD871ACDBAB84658DC844F18B45F7295094B03BD36B1F4B425A9'),
    @('https://www.ffmpeg.org/ffmpeg-devel.asc','397B3BECEDCD5A98769967FF1FF8501DDC89F8368B8F766E4701377D7DBAABE5'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-aom-3.15.0-1-any.pkg.tar.zst','B32B68ED8537C5CEF380494CF4AF48E01273661DD5B2570E38BF14F80892AC61'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-libvpx-1.17.0-1-any.pkg.tar.zst','8FF6919DBB88EA26CE28D22FD81D6F0D6E200CF56A4DD81FFC2D33124FE98024'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-opus-1.6.1-2-any.pkg.tar.zst','BB114BCF26225F50F874497EBFD6B8224FFF2A20BDA3F8EE45CD07CE5F02657B'),
    @('https://mirror.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst','AF0BDBA17F06FE037F0194069ADAA31A8FE45F1A11381501896AEA1FAE37BD5D'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-nasm-3.02-1-any.pkg.tar.zst','E60CF678BEDEE3D6C9A9264F8DEC5897520E5FF7E3AE80C2B90B2628C5F51D58'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-pkgconf-1~3.0.11-2-any.pkg.tar.zst','68C157C7C19F9015B6B0B65CFEF16046DA6DD628AEA816E2CDFE385B710263AD'),
    @('https://mirror.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-libwinpthread-13.0.0.r560.g3197fc7d6-1-any.pkg.tar.zst','C1A84FA9F0F3F1DC26C5E9CCC0AAE184806985B06DDE0984B0521D89E387FE2B'),
    @('https://mirror.msys2.org/mingw/sources/mingw-w64-aom-3.15.0-1.src.tar.zst','C6AAF5A78E5965B5F4D54DCD58EA108ACAA82FCC0F251262B6D72BA5220EF3FC'),
    @('https://mirror.msys2.org/mingw/sources/mingw-w64-libvpx-1.17.0-1.src.tar.zst','7B322FFFE50501D26BBBB489135E5001F6096DA2529AD992C712C00C94BB38DD'),
    @('https://mirror.msys2.org/mingw/sources/mingw-w64-opus-1.6.1-2.src.tar.zst','65AF241B547B36A0B1B07561B41F35EA6F8825712A22707005B9E783BD9AFC72'),
    @('https://mirror.msys2.org/mingw/sources/mingw-w64-winpthreads-13.0.0.r560.g3197fc7d6-1.src.tar.zst','7CB3D654D8969013231C51417640C3558F9C7A4B039A4FA1EA11C660445422C7')
)
foreach ($item in $inputs) {
    $name = $item[0].Split('/')[-1]
    $file = Join-Path $downloads $name
    if (-not (Test-Path $file)) {
        & curl.exe --fail --location --silent --show-error $item[0] --output $file
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $name." }
    }
    if ((Get-FileHash -LiteralPath $file).Hash -ne $item[1]) { throw "SHA-256 mismatch for $name." }
    if ($name.EndsWith('.pkg.tar.zst')) {
        & tar.exe -xf $file -C $sysroot
        if ($LASTEXITCODE -ne 0) { throw "Extraction failed for $name." }
    }
}
if (-not (Test-Path $Gpg)) { throw 'Provide gpg.exe to verify the FFmpeg release signature.' }
New-Item -ItemType Directory -Force (Join-Path $root 'gpg') | Out-Null
Push-Location $root
try {
    & $Gpg --homedir ./gpg --batch --import ./downloads/ffmpeg-devel.asc
    if ($LASTEXITCODE -ne 0) { throw 'Could not import the pinned FFmpeg signing key.' }
    $sig = & $Gpg --homedir ./gpg --batch --status-fd 1 --verify ./downloads/ffmpeg-7.1.4.tar.xz.asc ./downloads/ffmpeg-7.1.4.tar.xz
    if ($LASTEXITCODE -ne 0 -or -not ($sig -match 'VALIDSIG FCF986EA15E6E293A5644F10B4322F04D67658D8 ')) {
        throw 'The FFmpeg source signature did not match the pinned signing fingerprint.'
    }
}
finally { Pop-Location }
if (-not (Test-Path (Join-Path $root 'ffmpeg-7.1.4/configure'))) {
    & tar.exe -xf (Join-Path $downloads 'ffmpeg-7.1.4.tar.xz') -C $root
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg source extraction failed.' }
}
$buildScript = Join-Path $root 'build-ffmpeg.sh'
@'
#!/usr/bin/env bash
set -eu
root=$(cd "${0%/*}" && pwd)
export PATH="/ucrt64/bin:/usr/bin:$root/sysroot/ucrt64/bin"
export PKG_CONFIG_LIBDIR="$(cygpath -am "$root/sysroot/ucrt64/lib/pkgconfig")"
export PKG_CONFIG_PATH=
prefix=$(cygpath -am "$root/sysroot/ucrt64")
cd "$root/ffmpeg-7.1.4"
./configure --prefix="$root/ffmpeg-install" --arch=x86_64 --target-os=mingw32 --cc=gcc \
  --enable-shared --disable-static --disable-autodetect --disable-gpl --disable-nonfree --disable-version3 \
  --disable-ffplay --disable-doc --disable-debug --enable-libopus --enable-libvpx --enable-libaom --enable-d3d11va \
  --pkg-config=pkgconf --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib"
"$root/sysroot/usr/bin/make.exe" -j"$1"
"$root/sysroot/usr/bin/make.exe" install
'@ | Set-Content -LiteralPath $buildScript -NoNewline
$log = Join-Path $root 'ffmpeg-build.log'
& $bash -c 'exec /usr/bin/bash "$(/usr/bin/cygpath -u "$1")" "$2"' _ $buildScript $Jobs *> $log
if ($LASTEXITCODE -ne 0) {
    Get-Content $log -Tail 35
    throw "FFmpeg build failed. See $log."
}
Write-Output (Join-Path $root 'ffmpeg-install/bin')
