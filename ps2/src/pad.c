#include "pad.h"

#include <libpad.h>
#include <loadfile.h>

#define PORTS 2

static char buffers[PORTS][256] __attribute__((aligned(64)));
static int opened[PORTS];
static volatile int ready;

int pad_init(void)
{
    if (SifLoadModule("rom0:PADMAN", 0, NULL) < 0)
    {
        return -1;
    }

    padInit(0);
    for (int port = 0; port < PORTS; port++)
    {
        opened[port] = padPortOpen(port, 0, buffers[port]) != 0;
    }

    ready = 1;
    return 0;
}

void pad_close(void)
{
    if (!ready)
    {
        return;
    }

    ready = 0;
    for (int port = 0; port < PORTS; port++)
    {
        if (opened[port])
        {
            padPortClose(port, 0);
        }
    }

    padEnd();
}

unsigned pad_buttons(void)
{
    if (!ready)
    {
        return 0;
    }

    // A button counts when two reads in a row see it: the console reports presses nobody made, one read long.
    static unsigned last;
    unsigned held = 0;
    for (int port = 0; port < PORTS; port++)
    {
        if (!opened[port])
        {
            continue;
        }

        int state = padGetState(port, 0);
        struct padButtonStatus status;
        if ((state == PAD_STATE_STABLE || state == PAD_STATE_FINDCTP1) && padRead(port, 0, &status) != 0)
        {
            held |= 0xFFFFu ^ status.btns;
        }
    }

    unsigned steady = held & last;
    last = held;
    return steady;
}
