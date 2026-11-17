/* Offline fidelity oracle: the unmodified pinned PPU renders decoded bytes. */
#include "scanout.h"
#include "snes/ppu.h"
#include "sha256.h"
#include <stdio.h>

static bool range_u8(uint32_t v) { return v<=255; }
static bool range_u16(uint32_t v) { return v<=65535; }
static bool range_bit(uint32_t v) { return v<=1; }
static bool range_u32(uint32_t v) { (void)v; return true; }
static bool range_s32(uint32_t v) { (void)v; return true; }
static bool range_s16(uint32_t v) { return v<=32767 || v>=0xffff8000u; }
static int64_t signed_wire(uint32_t v) { return v<=INT32_MAX ? v : (int64_t)v-INT64_C(4294967296); }
#define value_u8(v) (v)
#define value_u16(v) (v)
#define value_bit(v) (v)
#define value_u32(v) (v)
#define value_s16(v) signed_wire(v)
#define value_s32(v) signed_wire(v)

bool hd2d_replay(const uint8_t *b, size_t n, uint8_t *rgba, char *error, size_t cap) {
  const char *why="invalid header";
  Ppu *p=NULL;
  uint8_t *pixels=NULL;
  const uint8_t *mem[3][HD2D_LINES]={{0}};
  unsigned counts[3]={0}, expected[3], last[3]={0};
  const unsigned sizes[3]={65536,512,544};
  if(!b || !rgba || n<HD2D_MEMORY_OFFSET+32 || n>HD2D_MAX_BYTES ||
     memcmp(b,"X2SCAN01",8) || hd2d_read32(b+8)!=1 || hd2d_read32(b+12)!=1 ||
     hd2d_read32(b+16)!=n || hd2d_read32(b+20)!=HD2D_LINES ||
     hd2d_read32(b+24)!=256 || hd2d_read32(b+28)!=224 ||
     hd2d_read32(b+32)!=HD2D_REGS || hd2d_read32(b+36)>1 ||
     memcmp(b+136,HD2D_PINNED_ENGINE,40) ||
     !hd2d_read64(b+64) || !hd2d_read64(b+72)) goto fail;
  bool session=false;
  for(unsigned i=40;i<56;++i) session|=b[i]!=0;
  if(!session) goto fail;
  for(unsigned i=96;i<176;++i)
    if(!((b[i]>='0'&&b[i]<='9')||(b[i]>='a'&&b[i]<='f'))) goto fail;
  for(unsigned k=0;k<3;++k) {
    expected[k]=hd2d_read32(b+180+4*k);
    if(!expected[k] || expected[k]>HD2D_LINES) goto fail;
  }
  uint8_t digest[32];
  sha256_compute(b,n-32,digest);
  why="checksum mismatch";
  if(memcmp(digest,b+n-32,32)) goto fail;
  why="invalid memory table";
  for(size_t pos=HD2D_MEMORY_OFFSET;pos<n-32;) {
    if(n-32-pos<12) goto fail;
    unsigned k=hd2d_read32(b+pos), id=hd2d_read32(b+pos+4), size=hd2d_read32(b+pos+8);
    pos+=12;
    if(k>2 || size!=sizes[k] || id!=counts[k] || id>=expected[k] || size>n-32-pos) goto fail;
    mem[k][counts[k]++]=b+pos; pos+=size;
  }
  for(unsigned k=0;k<3;++k) if(counts[k]!=expected[k]) goto fail;
  why="allocation failed";
  p=calloc(1,sizeof *p); pixels=calloc(1,HD2D_RGBA_BYTES);
  if(!p || !pixels) goto fail;
  ppu_reset(p);
  p->renderBuffer=pixels; p->renderPitch=1024; p->renderFlags=hd2d_read32(b+176);
  /* Regenerate derived caches, including an all-black first line. */
  p->lastBrightnessMult=p->lastMosaicModulo=255;
  for(unsigned line=0;line<HD2D_LINES;++line) {
    const uint8_t *row=b+HD2D_HEADER+line*HD2D_LINE_BYTES;
    why="invalid line order or memory reference";
    if(hd2d_read32(row)!=line) goto fail;
    unsigned ids[3];
    for(unsigned k=0;k<3;++k) {
      ids[k]=hd2d_read32(row+4+4*k);
      if(ids[k]>=counts[k] || (!line && ids[k]) || ids[k]<last[k] || ids[k]>last[k]+1) goto fail;
      last[k]=ids[k];
    }
    row+=16; why="invalid register value";
#define HD2D_FIELD(type, field) { uint32_t v=hd2d_read32(row); row+=4; \
  if(!range_##type(v)) { goto fail; } p->field=value_##type(v); }
#include "fields.inc"
#undef HD2D_FIELD
    const uint8_t *vram=mem[0][ids[0]], *cgram=mem[1][ids[1]], *oam=mem[2][ids[2]];
    for(unsigned i=0;i<32768;++i) p->vram[i]=(uint16_t)(vram[2*i]|vram[2*i+1]<<8);
    for(unsigned i=0;i<256;++i) {
      p->cgram[i]=(uint16_t)(cgram[2*i]|cgram[2*i+1]<<8);
      p->oam[i]=(uint16_t)(oam[2*i]|oam[2*i+1]<<8);
    }
    memcpy(p->highOam,oam+512,32);
    why="unsupported profile or offline environment";
    if(!hd2d_profile_supported(p)) goto fail;
    ppu_drawCapturedLine(p,(int)line,true);
  }
  why="unused memory version";
  for(unsigned k=0;k<3;++k) if(last[k]+1!=counts[k]) goto fail;
  for(unsigned i=0;i<HD2D_RGBA_BYTES;i+=4) {
    rgba[i]=pixels[i+2]; rgba[i+1]=pixels[i+1]; rgba[i+2]=pixels[i]; rgba[i+3]=255;
  }
  why="reference pixel mismatch";
  if(memcmp(rgba,b+HD2D_REFERENCE_OFFSET,HD2D_RGBA_BYTES)) goto fail;
  free(pixels); free(p);
  if(cap) snprintf(error,cap,"verified: 57344 RGBA pixels match pinned PPU");
  return true;
fail:
  free(pixels); free(p);
  if(cap) snprintf(error,cap,"%s",why);
  return false;
}
