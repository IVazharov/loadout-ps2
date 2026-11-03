# ps2sdk network manager, EE side

The EE half of ps2sdk's network manager (`libnetman`), which carries frames between the network adapter's driver on the IOP and the lwIP stack on the EE. Loadout Bay builds it from here instead of linking ps2sdk's `libnetman.a`.

- Source: [ps2sdk](https://github.com/ps2dev/ps2sdk) tag `v2.0.0` (commit `b12f8af37bd42ec13b1bafb7ab6e7bdcfb4b683b`), the same ps2sdk as the `ps2dev/ps2dev:v2.0.0` image that builds Loadout Bay.
- Files: `ee/network/netman/src/netman.c`, `rpc_client.c`, `rpc_server.c`, and `src/include/*.h`. `erl-support.c` is left out, since Loadout Bay is not an ERL.
- Licence: AFL-2.0, in `LICENSE` (ps2sdk's licence file).
- The IOP half, `netman.irx`, is ps2sdk's own and unchanged.

## The one change: receive buffers

`rpc_server.c` differs from ps2sdk's in its receive path only.

ps2sdk has the IOP write each received frame straight into an lwIP pool pbuf. In v2.0.0, lwIP's `MEM_ALIGNMENT` is 16, so a pool pbuf does not start on a 64-byte cache line: its last line is shared with the next pbuf's header. Writing that header (when the next pbuf is allocated) dirties the line while it still holds the old bytes of this pbuf. When the line is written back, by ps2sdk's write-back-and-invalidate after the frame's DMA or by an ordinary eviction, those old bytes land on the end of the new frame. lwIP is built without TCP checksum checks (`CHECKSUM_CHECK_TCP 0`), so nothing notices.

On the console this spoiled a few bytes at the end of many full-size frames: every MiB of a 2.3 GB game sent to Loadout Bay held a few wrong bytes, at most 10 in a row. PCSX2 does not emulate the EE data cache, so it does not happen there.

Here the IOP writes frames into `RxBuffers` instead: 64 buffers that are 64-byte aligned and padded to whole cache lines, which the CPU never writes. After each frame's DMA the receive thread invalidates the buffer's lines, copies the frame into a freshly allocated pbuf, and hands the buffer back to the IOP. The copy is 1.5 KB per frame.
