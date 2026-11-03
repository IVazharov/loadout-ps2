// The controllers, through the BIOS's PADMAN on its SIO2MAN (which the memory
// cards use too). Either port works.

#ifndef LOADOUT_PAD_H
#define LOADOUT_PAD_H

// Loads PADMAN and opens both ports. Returns 0, or -1 if PADMAN does not load.
int pad_init(void);

// The buttons held on either controller, as libpad's PAD_ bits; 0 before pad_init.
unsigned pad_buttons(void);

// Closes both ports before Loadout Bay leaves.
void pad_close(void);

#endif
