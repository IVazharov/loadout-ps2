# Loadout Bay, the PS2 side of Loadout. Builds with the ps2dev toolchain.
# The version is the whole of ../VERSION, shared with Loadout: major.minor.patch for a release, with a
# -dev.N suffix between releases (CONTRIBUTING.md).

VERSION ?= $(strip $(shell tr -d '\r' < ../VERSION))

# IOP modules from ps2sdk, embedded in the ELF: file I/O, USB storage, network, hard disk, and PFS.
IRX = iomanX fileXio bdm bdmfs_fatfs usbd usbmass_bd ps2dev9 netman smap ps2atad ps2fs
IRX_DIR = $(PS2SDK)/iop/irx

# The hard disk driver is ps2sdk's, built here with HDLoader partition support (see its README).
HDD_DIR = third_party/ps2sdk-apa

# The EE side of ps2sdk's network manager, built here with whole-cache-line receive buffers (see its README).
NETMAN_DIR = third_party/ps2sdk-netman
NETMAN_OBJS = $(NETMAN_DIR)/src/netman.o $(NETMAN_DIR)/src/rpc_client.o $(NETMAN_DIR)/src/rpc_server.o

# ps2sdk's ELF loader 2 starts a new Loadout Bay from memory; its buffer functions are declared here (see its README).
ELF_LOADER_DIR = third_party/ps2sdk-elf-loader2

EE_BIN = loadout-bay.elf
EE_OBJS = src/main.o src/bench.o src/config.o src/control.o src/discovery.o src/disk.o src/files.o src/games.o src/install.o src/layout.o src/font_data.o src/gfx.o src/netscreen.o src/pad.o src/ui.o src/widgets.o src/upgrade.o src/wire.o src/lwip_clock.o $(NETMAN_OBJS) $(IRX:%=build/%_irx.o) build/loadout_hdd_irx.o
EE_LIBS = -lgskit -ldmakit -lpad -lpatches -lfileXio -lps2ip -lelf-loader2 -lm
EE_CFLAGS += -Wall -Wextra -Werror -DLOADOUT_VERSION=\"$(VERSION)\"
EE_INCS += -I$(NETMAN_DIR)/src/include -I$(ELF_LOADER_DIR) -I$(GSKIT)/include
EE_LDFLAGS += -L$(GSKIT)/lib
# lwIP's timers take their time from src/lwip_clock.c, not from ps2sdk's sys_now, which jumps after 35 minutes.
EE_LDFLAGS += -Wl,--wrap=sys_now

all: $(EE_BIN)

build/%_irx.c: $(IRX_DIR)/%.irx
	@mkdir -p build
	bin2c $< $@ $*_irx

$(HDD_DIR)/loadout-hdd.irx: $(wildcard $(HDD_DIR)/src/*) $(HDD_DIR)/Makefile
	$(MAKE) -C $(HDD_DIR)

build/loadout_hdd_irx.c: $(HDD_DIR)/loadout-hdd.irx
	@mkdir -p build
	bin2c $< $@ loadout_hdd_irx

clean:
	rm -rf $(EE_BIN) $(EE_OBJS) build
	$(MAKE) -C $(HDD_DIR) clean

include $(PS2SDK)/samples/Makefile.pref
include $(PS2SDK)/samples/Makefile.eeglobal
