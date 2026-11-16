# LZMA SDK

Loadout's LZMA encoder for chd files, `src/Loadout.Core/Images/Chd/ChdLzmaEncoder.cs`, is ported from the C# encoder of Igor Pavlov's [LZMA SDK](https://www.7-zip.org/sdk.html) (`CS/7zip/Compress/LZMA/LzmaEncoder.cs`, `LZ/LzBinTree.cs` and `RangeCoder`, as kept in MAME's `3rdparty/lzma`). It works on one hunk in memory and keeps chdman's settings; the encoding itself is the SDK's.

- Licence: public domain. The SDK says: "LZMA SDK is written and placed in the public domain by Igor Pavlov. Anyone is free to copy, modify, publish, use, compile, sell, or distribute the original LZMA SDK code, either in source code form or as a compiled binary, for any purpose, commercial or non-commercial, and by any means."
