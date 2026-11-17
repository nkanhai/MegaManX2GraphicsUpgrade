#include "scanout.h"
#include "snes/ppu.h"
#include "snes/ws_shadow.h"
#include "sha256.h"
#include <stdio.h>
#ifdef _WIN32
#include <direct.h>
#define make_directory(p) _mkdir(p)
#define remove_directory(p) _rmdir(p)
#else
#include <sys/stat.h>
#include <unistd.h>
#define make_directory(p) mkdir(p, 0700)
#define remove_directory(p) rmdir(p)
#endif

struct Hd2dCapture {
  uint8_t *bytes;
  size_t limit, used;
  void (*release)(void *);
  Hd2dStatus status;
  unsigned lines, count[3];
  size_t last[3];
  uint64_t copied, compared;
  uint32_t flags, pitch;
  const uint8_t *surface; /* Checked for stability; never published. */
};
static const size_t memory_sizes[3] = {65536, 512, 544};

uint32_t hd2d_read32(const uint8_t *p) {
  return (uint32_t)p[0] | (uint32_t)p[1]<<8 | (uint32_t)p[2]<<16 | (uint32_t)p[3]<<24;
}
uint64_t hd2d_read64(const uint8_t *p) {
  return hd2d_read32(p) | (uint64_t)hd2d_read32(p+4)<<32;
}
void hd2d_write32(uint8_t *p, uint32_t v) {
  for (unsigned i=0; i<4; ++i) p[i]=(uint8_t)(v>>(8*i));
}
static void write64(uint8_t *p, uint64_t v) {
  hd2d_write32(p, (uint32_t)v); hd2d_write32(p+4, (uint32_t)(v>>32));
}
static bool zero(const void *data, size_t n) {
  const uint8_t *p=data;
  for (size_t i=0;i<n;++i) if(p[i]) return false;
  return true;
}

bool hd2d_profile_supported(const Ppu *p) {
  const uint16_t endian=1;
  if (!p || *(const uint8_t *)&endian != 1 || PPU_mode(p)!=1 || p->mosaic ||
      p->setini || p->frameOverscan || p->frameInterlace ||
      !p->renderBuffer || p->renderPitch<1024 || p->renderPitch>16384 ||
      p->renderPitch%4 || (uintptr_t)p->renderBuffer%4 ||
      p->renderFlags & ~(kPpuRenderFlags_NewRenderer|kPpuRenderFlags_NoSpriteLimits) ||
      p->extraLeftCur || p->extraRightCur || p->extraLeftRight || p->extraBottomCur ||
      p->renderVram || p->extraObjects || p->extraObjectCount ||
      p->mode7Hd.pixels || p->widescreenLineEnhancer ||
      p->wsHudSplitHeight || p->wsHudOamSlots || p->wsHudOamSlots2 ||
      p->wsBg3WidenY || p->wsLayerWidenMask || p->wsMode2CaptureLayer ||
      p->wsLayerClamp || p->wsLayerMirror || p->wsLayerRepeat ||
      p->wsWindowExpandLayers || p->wsWindowExpandWindows ||
      p->wsOamLeftHintStrict || p->wsOamRightHintStrict ||
      g_snes_ppu_dbg_layer_mask != 0xff) return false;
#define EMPTY(member) if (!zero(p->member,sizeof p->member)) return false
  EMPTY(wsClampY0); EMPTY(wsClampY1); EMPTY(wsRepeatY0); EMPTY(wsRepeatY1);
  EMPTY(wsStretchY0); EMPTY(wsStretchY1); EMPTY(wsAnchorY0); EMPTY(wsAnchorY1);
  EMPTY(wsMarginGapL); EMPTY(wsMarginGapR); EMPTY(wsWorldY0); EMPTY(wsWorldY1);
  EMPTY(wsElasticNSeg); EMPTY(wsViewportInsetL); EMPTY(wsViewportInsetR);
#undef EMPTY
  for (int i=0;i<4;++i) if(WsShadowLayerActive(i)) return false;
  for (int i=0;i<kPpuOverlaySource_Count;++i)
    if(p->overlayRenderBuffer[i] || p->overlayRenderPitch[i]) return false;
  return true;
}

Hd2dCapture *hd2d_capture_new(size_t limit, void *(*allocate)(size_t), void (*release)(void *)) {
  if (limit<HD2D_MEMORY_OFFSET+32 || limit>HD2D_MAX_BYTES || !!allocate!=!!release) return NULL;
  if (!allocate) { allocate=malloc; release=free; }
  Hd2dCapture *c=allocate(sizeof *c);
  if (!c) return NULL;
  memset(c,0,sizeof *c);
  c->bytes=allocate(limit);
  if (!c->bytes) { release(c); return NULL; }
  c->limit=limit; c->release=release;
  return c;
}
void hd2d_capture_free(Hd2dCapture *c) {
  if(c) { c->release(c->bytes); c->release(c); }
}
Hd2dStatus hd2d_capture_status(const Hd2dCapture *c) { return c ? c->status : HD2D_LIMIT; }
const char *hd2d_status_name(Hd2dStatus s) {
  static const char *names[]={"idle","recording","complete","unsupported-profile",
    "line-order-or-surface-changed","capacity","file-error","invalid-identity"};
  return (unsigned)s<sizeof names/sizeof *names ? names[s] : "unknown";
}
static bool revision(const char *s) {
  for(int i=0;i<40;++i) if(!((s[i]>='0'&&s[i]<='9')||(s[i]>='a'&&s[i]<='f'))) return false;
  return s[40]==0;
}
Hd2dStatus hd2d_capture_begin(Hd2dCapture *c, const Hd2dIdentity *id, const Ppu *p) {
  if(!c) return HD2D_LIMIT;
  c->status=HD2D_IDENTITY;
  if(!id || !id->sequence || !id->draw || zero(id->session,16) ||
     !revision(id->producer) || !revision(id->engine) || id->source>1) return c->status;
  if(!hd2d_profile_supported(p)) return c->status=HD2D_UNSUPPORTED;
  memset(c->bytes,0,HD2D_MEMORY_OFFSET);
  c->lines=0; memset(c->count,0,sizeof c->count); c->copied=c->compared=0;
  c->used=HD2D_MEMORY_OFFSET; c->flags=p->renderFlags;
  c->surface=p->renderBuffer; c->pitch=p->renderPitch;
  uint8_t *b=c->bytes;
  memcpy(b,"X2SCAN01",8);
  hd2d_write32(b+8,1); hd2d_write32(b+12,1);
  hd2d_write32(b+20,HD2D_LINES); hd2d_write32(b+24,HD2D_WIDTH);
  hd2d_write32(b+28,HD2D_HEIGHT); hd2d_write32(b+32,HD2D_REGS);
  hd2d_write32(b+36,id->source); memcpy(b+40,id->session,16);
  write64(b+56,id->epoch); write64(b+64,id->sequence); write64(b+72,id->draw);
  write64(b+80,id->guest_frame); write64(b+88,id->master_cycles);
  memcpy(b+96,id->producer,40); memcpy(b+136,id->engine,40);
  hd2d_write32(b+176,p->renderFlags);
  memcpy(b+208,id->runtime_cart_sha256,32);
  return c->status=HD2D_RECORDING;
}
static unsigned memory(Hd2dCapture *c, unsigned kind, const uint16_t *words, const uint8_t *high) {
  size_t n=memory_sizes[kind], word_bytes=kind==2 ? 512 : n;
  if(c->count[kind]) {
    c->compared+=n;
    const uint8_t *old=c->bytes+c->last[kind];
    if(!memcmp(old,words,word_bytes) && (!high || !memcmp(old+word_bytes,high,32)))
      return c->count[kind]-1;
  }
  if(c->count[kind]==HD2D_LINES || c->used+12+n+32>c->limit) {
    c->status=HD2D_LIMIT; return 0;
  }
  uint8_t *dst=c->bytes+c->used;
  unsigned id=c->count[kind]++;
  hd2d_write32(dst,kind); hd2d_write32(dst+4,id); hd2d_write32(dst+8,(uint32_t)n);
  dst+=12; c->last[kind]=c->used+12;
  for(size_t i=0;i<word_bytes/2;++i) { dst[i*2]=(uint8_t)words[i]; dst[i*2+1]=(uint8_t)(words[i]>>8); }
  if(high) memcpy(dst+word_bytes,high,32);
  c->used+=12+n; c->copied+=n;
  return id;
}
Hd2dStatus hd2d_capture_line(Hd2dCapture *c, const Ppu *p, unsigned line) {
  if(!c || c->status!=HD2D_RECORDING) return hd2d_capture_status(c);
  if(line!=c->lines || line>=HD2D_LINES || p->renderFlags!=c->flags ||
     p->renderBuffer!=c->surface || p->renderPitch!=c->pitch) return c->status=HD2D_ORDER;
  if(!hd2d_profile_supported(p)) return c->status=HD2D_UNSUPPORTED;
  uint8_t *row=c->bytes+HD2D_HEADER+line*HD2D_LINE_BYTES;
  hd2d_write32(row,line);
  hd2d_write32(row+4,memory(c,0,PpuRenderVram(p),NULL));
  hd2d_write32(row+8,memory(c,1,p->cgram,NULL));
  hd2d_write32(row+12,memory(c,2,p->oam,p->highOam));
  row+=16;
#define HD2D_FIELD(type, field) hd2d_write32(row,(uint32_t)p->field); row+=4;
#include "fields.inc"
#undef HD2D_FIELD
  c->copied+=4*HD2D_REGS; ++c->lines;
  return c->status;
}
Hd2dStatus hd2d_capture_finish(Hd2dCapture *c, const Ppu *p) {
  if(!c || c->status!=HD2D_RECORDING) return hd2d_capture_status(c);
  if(c->lines!=HD2D_LINES || p->renderBuffer!=c->surface || p->renderPitch!=c->pitch)
    return c->status=HD2D_ORDER;
  /* Canonical RGBA, excluding renderer-dependent/unwritten X padding. */
  uint8_t *dst=c->bytes+HD2D_REFERENCE_OFFSET;
  for(unsigned y=0;y<HD2D_HEIGHT;++y) for(unsigned x=0;x<HD2D_WIDTH;++x) {
    const uint8_t *src=c->surface+(size_t)y*c->pitch+x*4;
    *dst++=src[2]; *dst++=src[1]; *dst++=src[0]; *dst++=255;
  }
  c->copied+=HD2D_RGBA_BYTES;
  hd2d_write32(c->bytes+16,(uint32_t)(c->used+32));
  for(unsigned i=0;i<3;++i) hd2d_write32(c->bytes+180+4*i,c->count[i]);
  write64(c->bytes+192,c->copied); write64(c->bytes+200,c->compared);
  sha256_compute(c->bytes,c->used,c->bytes+c->used); c->used+=32;
  return c->status=HD2D_COMPLETE;
}
const uint8_t *hd2d_capture_data(const Hd2dCapture *c, size_t *length) {
  *length=c && c->status==HD2D_COMPLETE ? c->used : 0;
  return *length ? c->bytes : NULL;
}
Hd2dStatus hd2d_capture_publish(Hd2dCapture *c, const char *dir) {
  if(!c || c->status!=HD2D_COMPLETE) return hd2d_capture_status(c);
  char tmp[1100], final[1100];
  if(!dir || !dir[0] || strlen(dir)>1024) return HD2D_IO;
  snprintf(tmp,sizeof tmp,"%s/capture.tmp",dir);
  snprintf(final,sizeof final,"%s/capture.x2scan",dir);
  if(make_directory(dir)) return HD2D_IO;
  FILE *f=fopen(tmp,"wb");
  bool ok=false;
  if(f) { ok=fwrite(c->bytes,1,c->used,f)==c->used; if(fclose(f)) ok=false; }
  if(ok && rename(tmp,final)==0) return HD2D_COMPLETE;
  remove(tmp); remove_directory(dir);
  return HD2D_IO; /* In-memory record remains complete for explicit retry. */
}
