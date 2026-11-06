// lwIP's clock, without ps2sdk's 35-minute fault.
//
// ps2sdk's lwIP takes its time from sys_now(), which it computes as clock() / 1000. On the EE clock_t
// is a signed 32-bit count of microseconds, so 2^31 us (35.8 minutes) after Loadout Bay starts, that
// time jumps back by 71.6 minutes. lwIP then reads every pending timer as far in the future, and none
// fires for that long, TCP's retransmission among them: a reply that loses a frame never arrives, so
// Loadout times out on the game list while short replies still work.
//
// The link wraps sys_now (-Wl,--wrap=sys_now, see the Makefile), so lwIP's timers call this instead.
// It counts milliseconds from the EE's 64-bit timer, so it wraps only after 49 days, and cleanly, as
// lwIP's unsigned time arithmetic expects.

#include <kernel.h>
#include <tamtypes.h>
#include <timer.h>

u32 __wrap_sys_now(void);

u32 __wrap_sys_now(void)
{
    return (u32)(GetTimerSystemTime() / (kBUSCLK / 1000u));
}
