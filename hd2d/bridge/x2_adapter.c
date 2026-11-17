#include "scanout.h"
#include "x2_adapter.h"
#include "hotkey.h"
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
#ifdef _WIN32
#include <direct.h>
#define private_mkdir(p) _mkdir(p)
#else
#include <sys/stat.h>
#define private_mkdir(p) mkdir(p,0700)
#endif

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
static bool initialized, consumed, interactive;
static uint64_t draw, target=1;
static const char *directory;
static char destination[1025];
enum { MAX_SESSION_ATTEMPTS=64 };
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
  unsigned nonzero=0;
  for(unsigned i=0;i<16;++i) nonzero|=identity.session[i];
  return nonzero!=0;
}
static void configure(void) {
  initialized=true;
  directory=getenv("HD2D_CAPTURE_DIR");
  if(!directory || !*directory) { consumed=true; return; }
  const char *mode=getenv("HD2D_CAPTURE_INTERACTIVE");
  interactive=mode && !strcmp(mode,"1");
  const char *selection=getenv("HD2D_CAPTURE_DRAW");
  bool valid=(!mode || interactive) && !(interactive && selection);
  if(selection) {
    for(const char *s=selection;*s;++s) if(*s<'0'||*s>'9') valid=false;
    char *end; errno=0;
    target=strtoull(selection,&end,10);
    valid=valid && *selection && !*end && !errno && target;
  }
  if(!valid || !parse_session(getenv("HD2D_CAPTURE_SESSION"))) {
    fprintf(stderr,"[hd2d] capture rejected: SESSION must be nonzero 32-digit lowercase hex; use positive DRAW or INTERACTIVE=1, never both\n");
    consumed=true;
  }
  if(!consumed && interactive) {
    if(strlen(directory)>900 || private_mkdir(directory)) {
      fprintf(stderr,"[hd2d] interactive disabled: capture root must be a NEW writable directory (path <=900 bytes)\n");
      consumed=true; return;
    }
    fprintf(stderr,"[hd2d] HD2D_INTERACTIVE_CAPTURE_V1 ready: Left Ctrl+Left Shift+F10; root=%s; limit=%u attempts\n",
      directory,MAX_SESSION_ATTEMPTS);
  }
}
void hd2d_x2_begin(void) {
  if(!initialized) configure();
  if(consumed) return;
  if(draw==UINT64_MAX) { consumed=true; return; }
  ++draw;
  if(interactive) {
    if(!hd2d_hotkey_poll()) return;
    ++identity.sequence; /* Failed attempts also own an identity, never reused. */
    snprintf(destination,sizeof destination,"%s/capture-%06" PRIu64 "-draw-%020" PRIu64,
      directory,identity.sequence,draw);
    if(identity.sequence==MAX_SESSION_ATTEMPTS) {
      consumed=true;
      fprintf(stderr,"[hd2d] session capture limit reached; final attempt=%u; gameplay continues\n",MAX_SESSION_ATTEMPTS);
    }
  } else {
    if(draw!=target) return;
    consumed=true; /* Exactly one attempt, including unsupported/error cases. */
    identity.sequence=1;
  }
  const char *host_failure=NULL;
  if(snes_runahead_frames()) host_failure="runahead_frames: requires 0";
  else if(snes_netplay_active()) host_failure="netplay_active: requires false";
  else if(snes_netplay_rollback_active()) host_failure="netplay_rollback_active: requires false";
  else if(snes_rewind_is_open()) host_failure="rewind_is_open: requires false";
  if(host_failure) {
    fprintf(stderr,"[hd2d] capture rejected: sequence=%" PRIu64 " draw=%" PRIu64 ": %s\n",identity.sequence,draw,host_failure); return;
  }
  clock_t start=clock();
  identity.epoch=RtlStateGeneration(); identity.draw=draw;
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
    fprintf(stderr,"[hd2d] capture rejected: sequence=%" PRIu64 " draw=%" PRIu64 ": %s: %s\n",identity.sequence,draw,hd2d_status_name(status),hd2d_capture_detail(capture));
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
  const char *output=interactive ? destination : directory;
  if(status==HD2D_COMPLETE) status=hd2d_capture_publish(capture,output);
  if(status!=HD2D_COMPLETE)
    fprintf(stderr,"[hd2d] failure detail: %s\n",status==HD2D_IO ?
      "publication failed: destination must be a new writable directory; check file/close/rename errors" : hd2d_capture_detail(capture));
  fprintf(stderr,"[hd2d] %s: sequence=%" PRIu64 " draw=%" PRIu64 " epoch=%" PRIu64 " bytes=%zu capture_cpu_ms=%.3f publish_cpu_ms=%.3f path=%s/capture.x2scan\n",
    hd2d_status_name(status),identity.sequence,draw,identity.epoch,bytes,1000*capture_cpu,
    1000.0*(clock()-start)/CLOCKS_PER_SEC,output);
  hd2d_capture_free(capture); capture=NULL;
}
void hd2d_x2_shutdown(void) {
  hd2d_hotkey_stop();
  hd2d_capture_free(capture); capture=NULL;
  /* Preserve sequence/draw across an in-process host relaunch. Reinstall the
   * SDL observer on the next draw; no stale request crosses this boundary. */
}
