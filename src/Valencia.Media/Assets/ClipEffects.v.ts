// Parameter header and integer source-sample maps are resolved by ClipEffectFrame.
function Rgb(value: u32): float3 {
    return float3(Convert<f32>(value & 255), Convert<f32>((value >> 8) & 255), Convert<f32>((value >> 16) & 255));
}
function Minimum(a: u32, b: u32): u32 {
    if (a < b) { return a; }
    return b;
}
function Tap(at: u32, first: u32, last: u32, step: u32, tap: u32): u32 {
    if (tap == 0) { return at - Minimum(at - first, step); }
    if (tap == 2) { return at + Minimum(last - at, step); }
    return at;
}
function TentWeight(tap: u32): f32 {
    if (tap == 1) { return 2.0; }
    return 1.0;
}

@compute
@numthreads(16, 16, 1)
function ClipEffectsMain(@builtin(dispatchThreadId) thread: uint3,
    @binding(0) readonly Source: StorageBuffer<u32>,
    @binding(1) readwrite Output: StorageBuffer<u32>,
    @binding(2) readonly P: StorageBuffer<u32>): void {
    let width = P[0];
    if (thread.x >= width || thread.y >= P[1]) { return; }
    let stride = P[2];
    let mode = P[3];
    let left = P[4];
    let top = P[5];
    let right = P[6];
    let bottom = P[7];
    let sizeX = P[8];
    let sizeY = P[16];
    let opacity = Bitcast<f32>(P[9]);
    let x = P[20 + thread.x];
    let y = P[20 + width + thread.y];
    var color = float3(0.0, 0.0, 0.0);
    let masked = x >= left && x < right && y >= top && y < bottom;
    if (x != 0xffffffff && y != 0xffffffff && !(masked && mode == 3)) {
        if (masked && mode == 2) {
            let sx = Minimum(right - 1, left + ((x - left) / sizeX) * sizeX + sizeX / 2);
            let sy = Minimum(bottom - 1, top + ((y - top) / sizeY) * sizeY + sizeY / 2);
            color = Rgb(Source[sy * stride + sx]);
        } else if (masked && mode == 1) {
            // Aurelian's three-tap tent weights (1, 2, 1) in both dimensions.
            static for (let dy of [0, 1, 2]) {
                static for (let dx of [0, 1, 2]) {
                    let sx = Tap(x, left, right - 1, sizeX, dx);
                    let sy = Tap(y, top, bottom - 1, sizeY, dy);
                    color = color + Rgb(Source[sy * stride + sx]) * (TentWeight(dx) * TentWeight(dy));
                }
            }
            color = color * 0.0625;
        } else {
            color = Rgb(Source[y * stride + x]);
        }
        if (P[15] != 0) {
            let mul = float3(Bitcast<f32>(P[10]), Bitcast<f32>(P[11]), Bitcast<f32>(P[12]));
            let half = float3(0.5, 0.5, 0.5);
            color = float3(color.x * mul.x, color.y * mul.y, color.z * mul.z) * (1.0 / 255.0);
            color = (color - half) * Bitcast<f32>(P[13]) + half;
            let luma = Dot(color, float3(0.2126, 0.7152, 0.0722));
            let gray = float3(luma, luma, luma);
            color = (gray + (color - gray) * Bitcast<f32>(P[14])) * 255.0;
        }
    }
    let index = thread.y * width + thread.x;
    let previous = Output[index];
    let value = color * opacity + Rgb(previous) * (1.0 - opacity);
    let r = U32(Clamp(value.x, 0.0, 255.0));
    let g = U32(Clamp(value.y, 0.0, 255.0));
    let b = U32(Clamp(value.z, 0.0, 255.0));
    Output[index] = (previous & 0xff000000) | r | (g << 8) | (b << 16);
}
