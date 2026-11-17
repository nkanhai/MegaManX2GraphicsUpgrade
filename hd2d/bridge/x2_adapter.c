#include "scanout.h"
#include "x2_adapter.h"
#include "common_rtl.h"
#include "cpu_state.h"
#include "snes_runahead.h"
#include "snes_rewind.h"
#include "netplay/snes_netplay.h"
#include "snes/snes.h"
#include "snes/cart.h"
#include "sha256.h"
#include <errno.h>
#include <inttypes.h>
#include <time.h>

#ifndef HD2D_PRODUCER
#define HD2D_PRODUCER "0000000000000000000000000000000000000000"
#endif
#ifndef HD2D_ENGINE
#define HD2D_ENGINE HD2D_PINNED_ENGINE
#endif
extern int snes_frame_counter;
extern Snes *g_snes;
/* No network API: these hooks are called only by the existing draw owner. */
static Hd2dCapture *capture;
static bool initialized, consumed;
static uint64_t draw, target=1;
static const char *directory;
static Hd2dIdentity identity;
static double capture_cpu;

static bool parse_session(const char *s) {
  if(!s || strlen(s)!=32) return false;
  for(unsigned i=0;i<16;++i) {
    unsigned value=0;
    for(unsigned j=0;j<2;++j) {
      char ch=s[2*i+j];
      unsigned nibble=ch>='0'&&ch<='9' ? (unsigned)(ch-'0') :
        ch>='a'&&ch<='f' ? (unsigned)(ch-'a'+10) : 16;
      if(nibble==16) return false;
      value=value*16+nibble;
    }
    identity.session[i]=(uint8_t)value;
  }
  return true;
}
static void configure(void) {
  initialized=true;
  directory=getenv("HD2D_CAPTURE_DIR");
  if(!directory || !*directory) { consumed=true; return; }
  const char *selection=getenv("HD2D_CAPTURE_DRAW");
  bool valid=true;
  if(selection) {
    for(const char *s=selection;*s;++s) if(*s<'0'||*s>'9') valid=false;
    char *end; errno=0;
    target=strtoull(selection,&end,10);
    valid=valid && *selection && !*end && !errno && target;
  }
  if(!valid || !parse_session(getenv("HD2D_CAPTURE_SESSION"))) {
    fprintf(stderr,"[hd2d] capture rejected: set a 32-digit lowercase hex SESSION and positive DRAW\n");
    consumed=true;
  }
}
void hd2d_x2_begin(void) {
  if(!initialized) configure();
  if(consumed) return;
  ++draw;
  if(draw!=target) return;
  consumed=true; /* Exactly one attempt, including unsupported/error cases. */
  if(snes_runahead_frames() || snes_netplay_active() || snes_netplay_rollback_active() || snes_rewind_is_open()) {
    fprintf(stderr,"[hd2d] capture rejected: speculative/netplay/rewind presentation\n"); return;
  }
  clock_t start=clock();
  identity.epoch=RtlStateGeneration(); identity.sequence=1; identity.draw=draw;
  identity.guest_frame=(uint64_t)(int64_t)snes_frame_counter;
  identity.master_cycles=g_cpu.master_cycles; identity.source=1;
  memcpy(identity.producer,HD2D_PRODUCER,41); memcpy(identity.engine,HD2D_ENGINE,41);
  /* Hash only the already-loaded cart, never open a ROM file. Cap work even
   * if a future title supplies a larger cart; zero explicitly means unknown. */
  if(g_snes && g_snes->cart && g_snes->cart->rom && g_snes->cart->romSize &&
     g_snes->cart->romSize<=16u*1024u*1024u)
    sha256_compute(g_snes->cart->rom,g_snes->cart->romSize,identity.runtime_cart_sha256);
  capture=hd2d_capture_new(HD2D_MAX_BYTES,NULL,NULL);
  Hd2dStatus status=hd2d_capture_begin(capture,&identity,g_ppu);
  capture_cpu=(double)(clock()-start)/CLOCKS_PER_SEC;
  if(status!=HD2D_RECORDING) {
    fprintf(stderr,"[hd2d] capture rejected: %s\n",hd2d_status_name(status));
    hd2d_capture_free(capture); capture=NULL;
  }
}
void hd2d_x2_line(unsigned line) {
  if(!capture) return;
  clock_t start=clock();
  hd2d_capture_line(capture,g_ppu,line);
  capture_cpu+=(double)(clock()-start)/CLOCKS_PER_SEC;
}
void hd2d_x2_finish(void) {
  if(!capture) return;
  clock_t start=clock();
  Hd2dStatus status=hd2d_capture_finish(capture,g_ppu);
  size_t bytes=0; hd2d_capture_data(capture,&bytes);
  capture_cpu+=(double)(clock()-start)/CLOCKS_PER_SEC;
  start=clock();
  if(status==HD2D_COMPLETE) status=hd2d_capture_publish(capture,directory);
  fprintf(stderr,"[hd2d] %s: draw=%" PRIu64 " epoch=%" PRIu64 " bytes=%zu capture_cpu_ms=%.3f publish_cpu_ms=%.3f\n",
    hd2d_status_name(status),draw,identity.epoch,bytes,1000*capture_cpu,
    1000.0*(clock()-start)/CLOCKS_PER_SEC);
  hd2d_capture_free(capture); capture=NULL;
}
