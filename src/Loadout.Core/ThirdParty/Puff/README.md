# puff

Loadout's deflate decoder, `src/Loadout.Core/Images/Chd/ChdInflate.cs`, is written after puff, Mark Adler's small inflate implementation, and altered from it: it is C#, and it allocates nothing per hunk.

- Source: `contrib/puff` in [zlib](https://github.com/madler/zlib).
- Licence: the zlib-style notice in `LICENSE`.
