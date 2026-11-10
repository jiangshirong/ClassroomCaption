# RNNoise 0.2

Unmodified upstream Xiph RNNoise full model, built from the v0.2 release:
https://github.com/xiph/rnnoise/releases/tag/v0.2

Source archive SHA-256:
`90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37`

`scripts/build-rnnoise.ps1` rebuilds `win-x64/rnnoise.dll` using MinGW-w64
x64 GCC. The scalar build requires no AVX and statically links libgcc.
The upstream copyright and redistribution terms are in `COPYING`.

The application calls the C API directly: 48 kHz mono, 480 float samples
per frame, with float amplitudes in the PCM16 range. RNNoise 0.2 has a
960-sample (20 ms) signal delay. The bypass path is delayed equally before
crossfading, and pending frames are drained at capture stop. No VAD gate is
applied: silence keeps its duration. There are no audio files or network
calls in this processor.
