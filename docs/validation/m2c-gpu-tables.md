# Measured Whisper block subgraphs

Five warm trials after one warmup. All times in milliseconds; memory in MiB. These are not full-model transcription measurements.

## tiny.en, activation rows 32

| Part | Tile rows | Mode | Weight budget/residency MiB | Wall median [min,max] | GPU compute | GPU weight copy | Policy CPU | Ring record CPU | Ring submit CPU | Ring wait CPU | Overlap | Dispatches |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| encoder | 64 | R0-resident-static2 | 6.763 | 29.54 [29.43,29.79] | 12.56 | 0.00 | 0.014 | 6.88 | 1.34 | 8.55 | 0.000 | 263 |
| encoder | 64 | R1-stream-dominatus | 0.750 | 35.53 [35.27,36.42] | 9.36 | 2.24 | 0.751 | 7.33 | 1.46 | 4.05 | 0.000 | 263 |
| encoder | 64 | R2-stream-static2 | 0.750 | 38.91 [37.65,40.33] | 9.64 | 2.25 | 0.009 | 6.92 | 1.33 | 7.30 | 0.000 | 263 |
| encoder | 64 | R3-stream-static4 | 1.501 | 52.09 [46.93,55.90] | 9.21 | 2.28 | 0.004 | 9.67 | 1.29 | 5.05 | 0.000 | 263 |
| encoder | 64 | R4-stream-stage-only | 0.750 | 38.31 [36.29,39.14] | 8.27 | 2.25 | 0.004 | 6.78 | 1.25 | 8.16 | 0.000 | 263 |
| encoder | 64 | R2-stream-static1 | 0.375 | 41.53 [39.77,42.05] | 7.85 | 2.22 | 0.004 | 5.10 | 1.32 | 18.22 | 0.000 | 263 |
| encoder | 256 | R0-resident-static2 | 8.266 | 22.12 [22.09,24.06] | 4.38 | 0.00 | 0.001 | 4.21 | 0.51 | 4.45 | 0.000 | 113 |
| encoder | 256 | R1-stream-dominatus | 3.002 | 32.66 [31.83,33.69] | 3.63 | 2.80 | 0.202 | 4.52 | 0.52 | 4.89 | 0.000 | 113 |
| encoder | 256 | R2-stream-static2 | 3.002 | 31.26 [30.53,31.86] | 3.65 | 2.81 | 0.001 | 4.53 | 0.52 | 4.42 | 0.000 | 113 |
| encoder | 256 | R3-stream-static4 | 3.002 | 35.70 [35.04,35.89] | 3.72 | 2.86 | 0.001 | 5.42 | 0.53 | 4.32 | 0.000 | 113 |
| encoder | 256 | R4-stream-stage-only | 3.002 | 31.11 [30.49,31.83] | 3.64 | 2.81 | 0.001 | 4.38 | 0.53 | 4.54 | 0.000 | 113 |
| encoder | 256 | R2-stream-static1 | 1.501 | 29.71 [29.02,30.00] | 3.52 | 2.73 | 0.001 | 2.99 | 0.52 | 10.17 | 0.000 | 113 |
| decoder-self-attention-mlp | 64 | R0-resident-static2 | 6.763 | 36.44 [35.90,36.82] | 12.39 | 0.00 | 0.004 | 5.40 | 1.07 | 8.55 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R1-stream-dominatus | 0.750 | 43.22 [41.31,43.29] | 8.98 | 2.28 | 0.218 | 5.66 | 1.14 | 5.77 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R2-stream-static2 | 0.750 | 42.38 [41.64,43.71] | 9.37 | 2.24 | 0.003 | 5.56 | 1.10 | 6.48 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R3-stream-static4 | 1.501 | 51.90 [51.15,53.25] | 9.11 | 2.28 | 0.003 | 8.09 | 1.19 | 4.44 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R4-stream-stage-only | 0.750 | 41.38 [40.95,41.49] | 8.71 | 2.27 | 0.003 | 5.49 | 1.13 | 5.97 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R2-stream-static1 | 0.375 | 46.05 [44.73,48.24] | 7.98 | 2.22 | 0.003 | 4.33 | 1.19 | 16.23 | 0.000 | 269 |
| decoder-self-attention-mlp | 256 | R0-resident-static2 | 8.266 | 28.82 [28.33,30.83] | 4.44 | 0.00 | 0.001 | 3.26 | 0.36 | 3.43 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R1-stream-dominatus | 3.002 | 39.47 [38.98,40.20] | 3.68 | 2.83 | 0.095 | 3.39 | 0.39 | 4.59 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R2-stream-static2 | 3.002 | 38.17 [38.15,38.77] | 3.66 | 2.79 | 0.001 | 3.47 | 0.38 | 4.52 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R3-stream-static4 | 3.002 | 39.50 [38.43,40.47] | 3.71 | 2.83 | 0.001 | 3.85 | 0.39 | 3.29 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R4-stream-stage-only | 3.002 | 37.62 [36.63,37.87] | 3.66 | 2.78 | 0.001 | 3.43 | 0.37 | 3.67 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R2-stream-static1 | 1.501 | 35.65 [33.90,38.13] | 3.80 | 2.74 | 0.001 | 2.32 | 0.38 | 8.22 | 0.000 | 119 |

## large-v3, activation rows 32

| Part | Tile rows | Mode | Weight budget/residency MiB | Wall median [min,max] | GPU compute | GPU weight copy | Policy CPU | Ring record CPU | Ring submit CPU | Ring wait CPU | Overlap | Dispatches |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| encoder | 64 | R0-resident-static2 | 75.044 | 59.93 [59.20,171.67] | 21.46 | 0.00 | 0.037 | 31.85 | 4.41 | 1.50 | 0.000 | 2545 |
| encoder | 64 | R1-stream-dominatus | 2.500 | 108.06 [106.45,110.05] | 20.05 | 3.49 | 2.237 | 33.49 | 5.04 | 1.05 | 0.000 | 2545 |
| encoder | 64 | R2-stream-static2 | 2.500 | 97.57 [95.61,110.79] | 24.09 | 5.62 | 0.036 | 31.70 | 4.40 | 1.62 | 0.000 | 2545 |
| encoder | 64 | R3-stream-static4 | 5.001 | 120.75 [120.26,122.85] | 22.58 | 3.59 | 0.035 | 38.90 | 4.36 | 1.26 | 0.000 | 2545 |
| encoder | 64 | R4-stream-stage-only | 2.500 | 99.20 [95.51,104.86] | 27.31 | 6.52 | 0.033 | 32.60 | 4.39 | 1.91 | 0.000 | 2545 |
| encoder | 64 | R2-stream-static1 | 1.250 | 120.95 [119.07,122.31] | 23.46 | 3.63 | 0.033 | 27.86 | 4.52 | 40.01 | 0.000 | 2545 |
| encoder | 256 | R0-resident-static2 | 77.935 | 39.86 [39.10,47.83] | 8.25 | 0.00 | 0.010 | 15.26 | 1.35 | 2.73 | 0.000 | 825 |
| encoder | 256 | R1-stream-dominatus | 7.970 | 87.17 [83.73,88.30] | 17.30 | 12.35 | 0.350 | 16.47 | 1.57 | 4.80 | 0.000 | 825 |
| encoder | 256 | R2-stream-static2 | 7.970 | 84.55 [83.62,87.58] | 16.81 | 12.34 | 0.013 | 16.37 | 1.54 | 4.41 | 0.000 | 825 |
| encoder | 256 | R3-stream-static4 | 15.941 | 114.91 [113.01,122.46] | 16.27 | 12.38 | 0.015 | 25.53 | 1.67 | 4.00 | 0.000 | 825 |
| encoder | 256 | R4-stream-stage-only | 7.970 | 88.54 [87.31,89.79] | 16.28 | 12.33 | 0.014 | 17.54 | 1.65 | 4.27 | 0.000 | 825 |
| encoder | 256 | R2-stream-static1 | 3.985 | 120.19 [106.40,123.03] | 16.45 | 12.32 | 0.017 | 13.28 | 1.77 | 49.65 | 0.000 | 825 |
| decoder-self-attention-mlp | 64 | R0-resident-static2 | 75.044 | 114.16 [84.25,128.73] | 54.84 | 0.00 | 0.026 | 29.42 | 3.45 | 30.28 | 0.000 | 2565 |
| decoder-self-attention-mlp | 64 | R1-stream-dominatus | 2.500 | 123.49 [121.91,125.42] | 24.32 | 3.64 | 0.621 | 30.57 | 4.09 | 1.28 | 0.000 | 2565 |
| decoder-self-attention-mlp | 64 | R2-stream-static2 | 2.500 | 129.58 [120.34,136.95] | 35.45 | 12.45 | 0.032 | 31.25 | 4.08 | 5.03 | 0.000 | 2565 |
| decoder-self-attention-mlp | 64 | R3-stream-static4 | 5.001 | 142.46 [141.59,148.40] | 22.38 | 3.61 | 0.034 | 37.57 | 4.12 | 1.16 | 0.000 | 2565 |
| decoder-self-attention-mlp | 64 | R4-stream-stage-only | 2.500 | 125.44 [121.50,127.27] | 25.55 | 3.69 | 0.032 | 30.64 | 4.03 | 1.37 | 0.000 | 2565 |
| decoder-self-attention-mlp | 64 | R2-stream-static1 | 1.250 | 151.89 [150.62,152.45] | 25.61 | 3.66 | 0.032 | 26.43 | 4.06 | 45.86 | 0.000 | 2565 |
| decoder-self-attention-mlp | 256 | R0-resident-static2 | 77.935 | 72.64 [71.88,74.79] | 18.98 | 0.00 | 0.009 | 13.39 | 1.01 | 10.71 | 0.000 | 845 |
| decoder-self-attention-mlp | 256 | R1-stream-dominatus | 7.970 | 112.93 [110.68,118.36] | 19.92 | 12.42 | 0.221 | 14.49 | 1.19 | 4.52 | 0.000 | 845 |
| decoder-self-attention-mlp | 256 | R2-stream-static2 | 7.970 | 112.85 [111.57,115.72] | 19.13 | 12.40 | 0.011 | 14.39 | 1.15 | 5.03 | 0.000 | 845 |
| decoder-self-attention-mlp | 256 | R3-stream-static4 | 15.941 | 139.67 [137.72,141.87] | 20.22 | 12.46 | 0.012 | 22.13 | 1.20 | 4.48 | 0.000 | 845 |
| decoder-self-attention-mlp | 256 | R4-stream-stage-only | 7.970 | 114.81 [114.35,115.83] | 22.53 | 12.48 | 0.010 | 14.28 | 1.14 | 5.34 | 0.000 | 845 |
| decoder-self-attention-mlp | 256 | R2-stream-static1 | 3.985 | 135.20 [133.44,145.82] | 18.87 | 12.39 | 0.011 | 10.38 | 1.16 | 40.51 | 0.000 | 845 |

## tiny.en, activation rows 1

| Part | Tile rows | Mode | Weight budget/residency MiB | Wall median [min,max] | GPU compute | GPU weight copy | Policy CPU | Ring record CPU | Ring submit CPU | Ring wait CPU | Overlap | Dispatches |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| encoder | 64 | R0-resident-static2 | 6.763 | 19.49 [19.18,27.71] | 2.07 | 0.00 | 0.014 | 6.86 | 1.43 | 0.47 | 0.000 | 263 |
| encoder | 64 | R1-stream-dominatus | 0.750 | 32.73 [32.41,33.06] | 4.95 | 1.18 | 0.773 | 7.44 | 1.56 | 1.23 | 0.000 | 263 |
| encoder | 64 | R2-stream-static2 | 0.750 | 31.86 [31.16,33.05] | 5.05 | 1.20 | 0.016 | 7.16 | 1.43 | 1.83 | 0.000 | 263 |
| encoder | 64 | R3-stream-static4 | 1.501 | 41.58 [41.14,42.18] | 5.39 | 1.20 | 0.005 | 9.54 | 1.26 | 1.37 | 0.000 | 263 |
| encoder | 64 | R4-stream-stage-only | 0.750 | 33.27 [31.97,35.28] | 5.42 | 1.19 | 0.004 | 6.62 | 1.26 | 3.62 | 0.000 | 263 |
| encoder | 64 | R2-stream-static1 | 0.375 | 35.43 [35.24,37.81] | 5.45 | 1.17 | 0.004 | 5.05 | 1.31 | 14.25 | 0.000 | 263 |
| encoder | 256 | R0-resident-static2 | 8.266 | 21.08 [20.18,22.87] | 2.91 | 0.00 | 0.002 | 4.29 | 0.51 | 3.09 | 0.000 | 113 |
| encoder | 256 | R1-stream-dominatus | 3.002 | 29.41 [28.78,29.84] | 2.40 | 1.43 | 0.165 | 4.42 | 0.52 | 2.98 | 0.000 | 113 |
| encoder | 256 | R2-stream-static2 | 3.002 | 28.52 [28.32,29.65] | 2.42 | 1.44 | 0.002 | 4.38 | 0.52 | 2.90 | 0.000 | 113 |
| encoder | 256 | R3-stream-static4 | 3.002 | 31.77 [31.34,33.23] | 2.53 | 1.44 | 0.002 | 5.26 | 0.53 | 2.35 | 0.000 | 113 |
| encoder | 256 | R4-stream-stage-only | 3.002 | 28.58 [27.92,28.88] | 2.53 | 1.44 | 0.001 | 4.44 | 0.52 | 2.63 | 0.000 | 113 |
| encoder | 256 | R2-stream-static1 | 1.501 | 26.56 [26.28,28.54] | 2.48 | 1.43 | 0.002 | 3.04 | 0.52 | 7.16 | 0.000 | 113 |
| decoder-self-attention-mlp | 64 | R0-resident-static2 | 6.763 | 33.93 [33.45,34.34] | 10.84 | 0.00 | 0.004 | 5.29 | 1.10 | 7.26 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R1-stream-dominatus | 0.750 | 40.21 [38.84,41.77] | 6.80 | 2.23 | 0.198 | 5.47 | 1.13 | 4.57 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R2-stream-static2 | 0.750 | 38.91 [38.61,39.95] | 6.81 | 2.23 | 0.003 | 5.50 | 1.12 | 4.44 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R3-stream-static4 | 1.501 | 49.22 [48.45,51.34] | 7.36 | 2.28 | 0.004 | 8.14 | 1.13 | 3.13 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R4-stream-stage-only | 0.750 | 38.05 [37.47,41.44] | 6.76 | 2.23 | 0.003 | 5.53 | 1.11 | 4.07 | 0.000 | 269 |
| decoder-self-attention-mlp | 64 | R2-stream-static1 | 0.375 | 43.48 [43.10,43.89] | 6.62 | 2.22 | 0.003 | 4.27 | 1.16 | 14.78 | 0.000 | 269 |
| decoder-self-attention-mlp | 256 | R0-resident-static2 | 8.266 | 27.77 [26.80,28.73] | 4.17 | 0.00 | 0.001 | 3.27 | 0.37 | 2.94 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R1-stream-dominatus | 3.002 | 37.08 [36.54,38.43] | 2.98 | 2.74 | 0.092 | 3.36 | 0.41 | 3.80 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R2-stream-static2 | 3.002 | 37.14 [36.45,38.29] | 3.27 | 2.77 | 0.001 | 3.44 | 0.39 | 3.80 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R3-stream-static4 | 3.002 | 39.62 [37.68,40.49] | 3.44 | 2.81 | 0.001 | 3.68 | 0.38 | 3.38 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R4-stream-stage-only | 3.002 | 37.34 [36.97,40.32] | 3.41 | 2.79 | 0.001 | 3.38 | 0.39 | 3.49 | 0.000 | 119 |
| decoder-self-attention-mlp | 256 | R2-stream-static1 | 1.501 | 34.14 [32.54,34.73] | 3.36 | 2.74 | 0.001 | 2.14 | 0.38 | 7.96 | 0.000 | 119 |

