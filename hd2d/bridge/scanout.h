/* X2 HD-2D diagnostic recorder. Project LICENSE applies; PPU stays upstream. */
#ifndef HD2D_SCANOUT_H
#define HD2D_SCANOUT_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
typedef struct Ppu Ppu;
enum {
  HD2D_LINES = 225, HD2D_WIDTH = 256, HD2D_HEIGHT = 224,
  HD2D_HEADER = 240, HD2D_RGBA_BYTES = 256 * 224 * 4,
  HD2D_MAX_BYTES = 4 * 1024 * 1024,
  HD2D_REGS = 0
#define HD2D_FIELD(type, field) + 1
#include "fields.inc"
#undef HD2D_FIELD
};
#define HD2D_LINE_BYTES (16 + 4 * HD2D_REGS)
#define HD2D_REFERENCE_OFFSET (HD2D_HEADER + HD2D_LINES * HD2D_LINE_BYTES)
#define HD2D_MEMORY_OFFSET (HD2D_REFERENCE_OFFSET + HD2D_RGBA_BYTES)
#define HD2D_PINNED_ENGINE "93fc677a2a9b688751d575ed1232343d83d3d2c0"
typedef struct {
  uint8_t session[16];
  uint64_t epoch, sequence, draw, guest_frame, master_cycles;
  /* Exactly 40 lowercase hex characters; use zeroes for unknown revisions. */
  char producer[41], engine[41];
  uint32_t source; /* 0: original synthetic graphics; 1: title runtime */
  uint8_t runtime_cart_sha256[32]; /* Zero: unavailable. Observed, possibly patched cart. */
} Hd2dIdentity;
typedef enum {
  HD2D_IDLE, HD2D_RECORDING, HD2D_COMPLETE, HD2D_UNSUPPORTED,
  HD2D_ORDER, HD2D_LIMIT, HD2D_IO, HD2D_IDENTITY
} Hd2dStatus;
typedef struct Hd2dCapture Hd2dCapture;
/* Owner thread only. NULL allocator functions select malloc/free. */
Hd2dCapture *hd2d_capture_new(size_t limit,
                            void *(*allocate)(size_t), void (*release)(void *));
void hd2d_capture_free(Hd2dCapture *capture);
Hd2dStatus hd2d_capture_begin(Hd2dCapture *, const Hd2dIdentity *, const Ppu *);
Hd2dStatus hd2d_capture_line(Hd2dCapture *, const Ppu *, unsigned line);
Hd2dStatus hd2d_capture_finish(Hd2dCapture *, const Ppu *);
Hd2dStatus hd2d_capture_status(const Hd2dCapture *);
const char *hd2d_status_name(Hd2dStatus);
/* Owned bytes remain immutable until explicit begin/free. Never live PPU data. */
const uint8_t *hd2d_capture_data(const Hd2dCapture *, size_t *length);
/* Creates a NEW directory, then publishes capture.x2scan last. Never overwrites. */
Hd2dStatus hd2d_capture_publish(Hd2dCapture *, const char *new_directory);
bool hd2d_profile_supported(const Ppu *);
/* First failed requirement, including observed value. Never mutates the PPU. */
bool hd2d_profile_check(const Ppu *, char *detail, size_t capacity);
const char *hd2d_capture_detail(const Hd2dCapture *);
uint32_t hd2d_read32(const uint8_t *);
uint64_t hd2d_read64(const uint8_t *);
void hd2d_write32(uint8_t *, uint32_t);
/* Offline only: validate the complete container, reconstruct into a fresh PPU,
 * and compare canonical RGBA against its reference. No runtime global changes. */
bool hd2d_replay(const uint8_t *, size_t, uint8_t *rgba, char *error, size_t);
typedef enum { HD2D_REPLAY_INVALID, HD2D_REPLAY_MATCH, HD2D_REPLAY_MISMATCH } Hd2dReplayResult;
/* RGBA is valid on MATCH or MISMATCH; INVALID must never export an image. */
Hd2dReplayResult hd2d_reconstruct(const uint8_t *, size_t, uint8_t *rgba, char *error, size_t);
#endif
