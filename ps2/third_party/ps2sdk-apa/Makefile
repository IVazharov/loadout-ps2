# ps2sdk's APA hard disk driver, built as loadout-hdd.irx. The sources in src/
# are ps2sdk's, unchanged. Only these switches differ from ps2sdk's ps2hdd.irx:
#   APA_SUPPORT_HDL                 accepts the HDL partition type (0x1337) when creating a partition
#   APA_SUPPORT_IOCTL_GETPARTSTART  answers HIOCGETPARTSTART with a part's first sector on the disk
# Loadout Bay needs both to install games the way HDLGameInstaller lays them out.

IOP_BIN = loadout-hdd.irx
IOP_SRC_DIR = src/
IOP_OBJS_DIR = obj/
IOP_OBJS = hdd_fio.o hdd_blkio.o hdd.o imports.o password.o misc.o cache.o apa.o journal.o free.o

IOP_INCS += -Isrc
IOP_CFLAGS += -DIOMANX_OLD_NAME_COMPATIBILITY=0 -DIOMANX_OLD_NAME_ADDDELDRV=0
IOP_CFLAGS += -DAPA_USE_DEV9 -DAPA_USE_ATAD
IOP_CFLAGS += -DAPA_SUPPORT_HDL -DAPA_SUPPORT_IOCTL_GETPARTSTART

all: $(IOP_BIN)

clean:
	rm -rf $(IOP_BIN) $(IOP_BIN_ELF) $(IOP_BIN_STRIPPED_ELF) $(IOP_OBJS_DIR)

include $(PS2SDK)/samples/Makefile.pref
include $(PS2SDK)/samples/Makefile.iopglobal
