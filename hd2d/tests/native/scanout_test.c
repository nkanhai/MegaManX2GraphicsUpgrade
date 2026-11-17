#include "scanout.h"
#include "snes/ppu.h"
#include "snes/dma.h"
#include "snes/cart.h"
#include "common_rtl.h"
#include "cpu_state.h"
#include "sha256.h"
#include <time.h>
#include <assert.h>
#ifdef TEST_ADAPTER
#include "desktop/sdl_compat.h"
#include "x2_adapter.h"
#include <sys/stat.h>
static uint64_t test_epoch=7;
int FindCmdForSdlKey(SDL_Keycode key, SDL_Keymod mod) {
  (void)key; (void)mod; return getenv("TEST_KEY_CONFLICT") ? 1 : 0;
}
static void key(bool down, unsigned modifiers) {
  SDL_Event event={0}; event.type=down ? SDL_KEYDOWN : SDL_KEYUP;
  SDL_Window *window=SDL_GetKeyboardFocus();
  event.key.windowID=window ? SDL_GetWindowID(window) : 0;
  event.key.keysym.sym=SDLK_F10; event.key.keysym.mod=modifiers;
  if(SDL_PushEvent(&event)!=1) { fprintf(stderr,"SDL event injection failed\n"); exit(1); }
}
#endif

Ppu *g_ppu;
Dma *g_dma;
CpuState g_cpu;
extern int snes_frame_counter;
extern bool test_shadow_active;
extern Snes *g_snes;
void X2DrawPpuFrame(void);
static unsigned calls, inits, starts;
static bool changes=true, flooding, priority_raster;
#ifndef TEST_ADAPTER
static bool armed;
static Hd2dCapture *capture;
#endif
static Hd2dIdentity id;
static _Alignas(4) uint8_t pixels[224][1056]; /* Source pitch and untouched padding. */
#if defined(HD2D_SCANOUT_CAPTURE) && !defined(TEST_ADAPTER)
static uint8_t rgba[HD2D_RGBA_BYTES];
#endif
#define CHECK(x) do { if(!(x)) { fprintf(stderr,"FAIL %s:%d: %s\n",__FILE__,__LINE__,#x); exit(1); } } while(0)

/* Synthetic HDMA writes after the real ppu_runLine call, through the unchanged
 * title loop. Checking the next line's snapshots detects off-by-one hooks. */
void dma_startDma(Dma *dma,uint8_t mask,bool hdma) {
  CHECK(dma==g_dma && mask==g_snesrecomp_last_hdmaen && hdma); ++starts;
}
void SimpleHdma_Init(SimpleHdma *c,DmaChannel *dc) { (void)c; CHECK(dc==&g_dma->channel[inits++]); }
void SimpleHdma_DoLine(SimpleHdma *c) {
  (void)c;
  unsigned line=calls/8, channel=calls++%8;
  if(channel) return;
  if(priority_raster && line==19) g_ppu->oamaddl=2;
  if(!changes) return;
  if(line==63) { g_ppu->hScroll[0]=13; g_ppu->cgram[1]=0x03e0; }
  if(line==63 && getenv("TEST_MIDFRAME_MOSAIC")) g_ppu->mosaic=0x11;
  if(line==95) g_ppu->vram[0x1000]=0x55aa;
  if(line==127) { g_ppu->cgram[1]=0x001f; g_ppu->vScroll[1]=7; }
  if(line==143) g_ppu->oam[0]=(uint16_t)(150<<8|41);
  if(line==159) g_ppu->highOam[0]=2;
  if(line==175) { g_ppu->inidisp=7; g_ppu->window1left=61; }
  if(line==199) g_ppu->inidisp=0x80;
  if(line==207) g_ppu->inidisp=15;
  if(flooding) g_ppu->vram[0x1000]=(uint16_t)line;
}
#ifndef TEST_ADAPTER
void hd2d_x2_begin(void) { if(armed) CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_RECORDING); }
void hd2d_x2_line(unsigned line) { if(armed) hd2d_capture_line(capture,g_ppu,line); }
void hd2d_x2_finish(void) { if(armed) hd2d_capture_finish(capture,g_ppu); }
#else
/* Test the real one-shot adapter with explicit host policy stand-ins. */
int snes_runahead_frames(void) { return getenv("TEST_RUNAHEAD") ? 1 : 0; }
int snes_netplay_active(void) { return getenv("TEST_NETPLAY") ? 1 : 0; }
int snes_netplay_rollback_active(void) { return getenv("TEST_ROLLBACK") ? 1 : 0; }
int snes_rewind_is_open(void) { return getenv("TEST_REWIND") ? 1 : 0; }
uint64_t RtlStateGeneration(void) { return test_epoch; }
#endif

static void setup(unsigned flags) {
  memset(g_ppu,0,sizeof *g_ppu);
  ppu_reset(g_ppu);
  memset(pixels,0xa5,sizeof pixels);
  g_ppu->renderBuffer=&pixels[0][0]; g_ppu->renderPitch=sizeof pixels[0]; g_ppu->renderFlags=flags;
  g_ppu->inidisp=15; g_ppu->bgmode=1;
  if(getenv("TEST_PPU_MOSAIC")) g_ppu->mosaic=0x11;
  g_ppu->bgXsc[0]=0; g_ppu->bgXsc[1]=4; g_ppu->bgTileAdr=0x21;
  g_ppu->screenEnabled[0]=0x13; g_ppu->screenEnabled[1]=2;
  g_ppu->hScroll[1]=3; g_ppu->vScroll[0]=1;
  g_ppu->cgwsel=2; g_ppu->cgadsub=0x41;
  g_ppu->windowsel=2; g_ppu->screenWindowed[0]=1;
  g_ppu->window1left=25; g_ppu->window1right=47;
  g_ppu->obsel=3;
  g_ppu->m7matrix[0]=-32768; g_ppu->m7matrix[7]=32767;
  g_ppu->m7startX=-123456; g_ppu->m7startY=123456;
  for(unsigned i=0;i<256;++i) g_ppu->cgram[i]=(uint16_t)((i*131)&0x7fff);
  g_ppu->cgram[1]=0x001f;
  for(unsigned i=0;i<1024;++i) {
    g_ppu->vram[i]=(uint16_t)(i%2 | (i%3==0?0x4000:0) | (i%5==0?0x2000:0));
    g_ppu->vram[0x400+i]=(uint16_t)(i%2 | 0x400 | (i%7==0?0x8000:0));
  }
  for(unsigned tile=0;tile<2;++tile) for(unsigned y=0;y<8;++y) {
    g_ppu->vram[0x1000+tile*16+y]=(uint16_t)(tile?0xaa55:0x00ff);
    g_ppu->vram[0x2000+tile*16+y]=(uint16_t)(tile?0xff00:0x00cc);
    g_ppu->vram[0x6000+tile*16+y]=0x00ff;
  }
  for(unsigned i=0;i<128;++i) g_ppu->oam[i*2]=0xe000;
  for(unsigned i=0;i<3;++i) {
    g_ppu->oam[i*2]=(uint16_t)((40+40*i)<<8 | (55+50*i));
    g_ppu->oam[i*2+1]=(uint16_t)(0x3000 | (i<<9) | (i&1));
  }
  memset(g_dma,0,sizeof *g_dma); memset(&g_cpu,0,sizeof g_cpu);
  g_cpu.master_cycles=123456789;
  calls=inits=starts=0; snes_frame_counter=42;
}
static void draw(void) {
  X2DrawPpuFrame();
  CHECK(calls==225*8 && inits==8 && starts==1);
  CHECK(g_cpu.master_cycles==123456789);
  for(unsigned y=0;y<224;++y) for(unsigned x=1024;x<1056;++x) CHECK(pixels[y][x]==0xa5);
}
static void print_hash(void) {
  uint8_t digest[32]; sha256_compute(&pixels[0][0],sizeof pixels,digest);
  printf("surface_sha256="); for(unsigned i=0;i<32;++i) printf("%02x",digest[i]); puts("");
}
#ifndef TEST_ADAPTER
#ifdef HD2D_SCANOUT_CAPTURE
static unsigned allocation_count, fail_at;
static void *fault_allocate(size_t size) { return ++allocation_count==fail_at ? NULL : malloc(size); }
#endif
static void verify_case(unsigned flags) {
  setup(flags); armed=false; draw();
  Ppu *off=malloc(sizeof *off); CHECK(off); memcpy(off,g_ppu,sizeof *off);
  uint8_t *off_pixels=malloc(sizeof pixels); CHECK(off_pixels); memcpy(off_pixels,pixels,sizeof pixels);
  Dma off_dma=*g_dma; CpuState off_cpu=g_cpu;
  setup(flags); armed=true; draw();
  CHECK(!memcmp(off,g_ppu,sizeof *off)); CHECK(!memcmp(off_pixels,pixels,sizeof pixels));
  CHECK(!memcmp(&off_dma,g_dma,sizeof off_dma)); CHECK(!memcmp(&off_cpu,&g_cpu,sizeof off_cpu));
  free(off); free(off_pixels);
#ifdef HD2D_SCANOUT_CAPTURE
  CHECK(hd2d_capture_status(capture)==HD2D_COMPLETE);
  size_t length; const uint8_t *b=hd2d_capture_data(capture,&length); char error[160];
  CHECK(hd2d_replay(b,length,rgba,error,sizeof error));
  CHECK(hd2d_read32(b+180)==2 && hd2d_read32(b+184)==3 && hd2d_read32(b+188)==3);
  CHECK(hd2d_read32(b+HD2D_HEADER+63*HD2D_LINE_BYTES+8)==0);
  CHECK(hd2d_read32(b+HD2D_HEADER+64*HD2D_LINE_BYTES+8)==1);
  CHECK(hd2d_read32(b+HD2D_HEADER+96*HD2D_LINE_BYTES+4)==1);
  CHECK(hd2d_read32(b+HD2D_HEADER+128*HD2D_LINE_BYTES+8)==2);
  CHECK(hd2d_read32(b+HD2D_HEADER+144*HD2D_LINE_BYTES+12)==1);
  CHECK(hd2d_read32(b+HD2D_HEADER+160*HD2D_LINE_BYTES+12)==2);
  uint8_t before[32], after[32]; sha256_compute(b,length,before);
  memset(g_ppu->vram,0,sizeof g_ppu->vram); memset(pixels,0,sizeof pixels);
  sha256_compute(b,length,after); CHECK(!memcmp(before,after,32));
  CHECK(hd2d_replay(b,length,rgba,error,sizeof error));
  printf("parity flags=%u bytes=%zu copied=%llu comparison_bytes=%llu: %s\n",flags,length,
    (unsigned long long)hd2d_read64(b+192),(unsigned long long)hd2d_read64(b+200),error);
#endif
}
static void verify_scene_contributors(void) {
  uint8_t reference[32], changed[32];
  setup(1); armed=false; draw(); sha256_compute(&pixels[0][0],sizeof pixels,reference);
  for(unsigned layer=0;layer<2;++layer) {
    setup(1);
    g_ppu->screenEnabled[0]&=(uint8_t)~(1u<<layer);
    g_ppu->screenEnabled[1]&=(uint8_t)~(1u<<layer);
    draw(); sha256_compute(&pixels[0][0],sizeof pixels,changed);
    CHECK(memcmp(reference,changed,32));
  }
  for(unsigned slot=0;slot<3;++slot) {
    setup(1); g_ppu->oam[slot*2]=0xe000;
    draw(); sha256_compute(&pixels[0][0],sizeof pixels,changed);
    CHECK(memcmp(reference,changed,32));
  }
}
static void replay_current(void) {
#ifdef HD2D_SCANOUT_CAPTURE
  size_t length; char error[256];
  const uint8_t *bytes=hd2d_capture_data(capture,&length);
  CHECK(bytes && hd2d_replay(bytes,length,rgba,error,sizeof error));
#endif
}
static void priority_scene(unsigned flags) {
  setup(flags); changes=false; armed=true;
  g_ppu->screenEnabled[0]=0x11; g_ppu->screenEnabled[1]=0;
  g_ppu->screenWindowed[0]=0; g_ppu->cgwsel=g_ppu->cgadsub=0;
  g_ppu->hScroll[0]=g_ppu->vScroll[0]=0;
  g_ppu->cgram[1]=0x7c00; g_ppu->cgram[129]=0x001f; g_ppu->cgram[145]=0x03e0;
  for(unsigned i=0;i<1024;++i) g_ppu->vram[i]=0;
  for(unsigned i=0;i<128;++i) g_ppu->oam[i*2]=0xe000;
  g_ppu->oam[0]=(16<<8)|32; g_ppu->oam[1]=0;
}
static void expect_rgb(unsigned y, unsigned r, unsigned g, unsigned b) {
  const uint8_t *pixel=pixels[y]+34*4;
  CHECK(pixel[2]==r && pixel[1]==g && pixel[0]==b);
}
static void verify_sprite_priorities(void) {
  unsigned cases=0;
  for(unsigned flags=0;flags<=9;++flags) if(!(flags&6)) {
    /* All four OBJ priorities against both BG1 priorities, with explicit
     * expected SNES priority ordering as well as whole-frame replay parity. */
    for(unsigned high=0;high<2;++high) for(unsigned priority=0;priority<4;++priority) {
      priority_scene(flags);
      for(unsigned i=0;i<1024;++i) g_ppu->vram[i]=(uint16_t)(high?0x2000:0);
      g_ppu->oam[1]=(uint16_t)(priority<<12);
      draw();
      bool front=priority>=(high?3u:2u);
      expect_rgb(18,front?255:0,0,front?0:255); replay_current(); ++cases;
    }
    /* Earlier OAM wins even when its OBJ priority is lower. Rotate the first
     * slot mid-raster: the following line must switch to the green object. */
    priority_scene(flags); g_ppu->screenEnabled[0]=0x10;
    g_ppu->oam[2]=g_ppu->oam[0]; g_ppu->oam[3]=0x3200;
    g_ppu->oamaddh=0x80; priority_raster=true;
    draw(); expect_rgb(18,255,0,0); expect_rgb(19,0,255,0); replay_current(); ++cases;
    priority_raster=false;
    /* Exercise range/time overflow and both sprite-limit policies, not just
     * the flag value in a scene that stays below the hardware limits. */
    priority_scene(flags); g_ppu->screenEnabled[0]=0x10;
    for(unsigned i=0;i<33;++i) {
      g_ppu->oam[2*i]=(16<<8)|32;
      g_ppu->oam[2*i+1]=(uint16_t)(0x3000 | ((i&1)<<9));
    }
    memset(g_ppu->highOam,0xaa,sizeof g_ppu->highOam);
    draw(); CHECK(g_ppu->rangeOver==!(flags&8)); CHECK(g_ppu->timeOver==!(flags&8));
    replay_current(); ++cases;
  }
  changes=true; printf("sprite priority/rotation/overflow cases: %u PASS\n",cases);
}
#endif
int main(int argc,char **argv) {
  (void)argv;
  g_ppu=calloc(1,sizeof *g_ppu); g_dma=calloc(1,sizeof *g_dma); CHECK(g_ppu && g_dma);
  memset(&id,0,sizeof id); memset(id.session,0x12,16); id.sequence=id.draw=1;
  id.epoch=7; id.guest_frame=42; id.master_cycles=123456789;
  memset(id.producer,'0',40); memcpy(id.engine,HD2D_PINNED_ENGINE,41);
#ifdef TEST_ADAPTER
  (void)argc; (void)argv;
  static uint8_t synthetic_cart[]="original synthetic identity";
  static Snes snes;
  static Cart cart;
  if(getenv("TEST_CART")) {
    cart.rom=synthetic_cart; cart.romSize=sizeof synthetic_cart-1;
    snes.cart=&cart; snes.ppu=g_ppu; g_snes=&snes;
  }
  if(getenv("TEST_INTERACTIVE")) {
    CHECK(SDL_Init(SDL_INIT_VIDEO)==0);
    unsigned frames=getenv("TEST_LIMIT") ? 68 : 6;
    for(unsigned frame=1;frame<=frames;++frame) {
      setup(1);
      if(frame>=4) { test_epoch=8; snes_frame_counter=0; g_ppu->cgram[2]^=0x7fff; }
      if(frame==2 && getenv("TEST_REJECT_ONCE")) g_ppu->mosaic=0x11;
      if(frame==2 || frame==4 || (getenv("TEST_LIMIT") && frame>1)) {
        if(frame==2 && getenv("TEST_COLLISION")) {
          char path[1100];
          snprintf(path,sizeof path,"%s/capture-000001-draw-00000000000000000002",getenv("HD2D_CAPTURE_DIR"));
          CHECK(mkdir(path,0700)==0);
        }
        /* Two complete presses before a draw must coalesce. */
        for(unsigned i=0;i<2;++i) { key(true,KMOD_LCTRL|KMOD_LSHIFT); key(false,0); }
      } else if(frame==3) { key(true,KMOD_LSHIFT); key(false,0); }
      draw(); print_hash();
      /* All pointer fields start null except the bound host surface. These
       * hashes compare the full PPU (including derived caches), CPU and DMA
       * against a separately compiled-out process driven by identical events. */
      Ppu state=*g_ppu; state.renderBuffer=NULL;
      uint8_t digest[32]; sha256_compute((const uint8_t *)&state,sizeof state,digest);
      printf("ppu_sha256="); for(unsigned i=0;i<32;++i) printf("%02x",digest[i]); puts("");
      sha256_compute((const uint8_t *)&g_cpu,sizeof g_cpu,digest);
      printf("cpu_sha256="); for(unsigned i=0;i<32;++i) printf("%02x",digest[i]); puts("");
      sha256_compute((const uint8_t *)g_dma,sizeof *g_dma,digest);
      printf("dma_sha256="); for(unsigned i=0;i<32;++i) printf("%02x",digest[i]); puts("");
      SDL_Event event; while(SDL_PollEvent(&event)) {}
    }
    hd2d_x2_shutdown(); SDL_Quit();
  } else for(unsigned i=0;i<3;++i) { setup(1); draw(); }
  print_hash();
#else
  capture=hd2d_capture_new(HD2D_MAX_BYTES,NULL,NULL); CHECK(capture);
  if(argc>1) {
    setup(1); armed=true; draw(); print_hash();
#ifdef HD2D_SCANOUT_CAPTURE
    CHECK(hd2d_capture_publish(capture,argv[1])==HD2D_COMPLETE);
    CHECK(hd2d_capture_publish(capture,argv[1])==HD2D_IO);
    CHECK(hd2d_capture_publish(capture,"/nonexistent-hd2d-parent/capture")==HD2D_IO);
#endif
  } else {
    verify_scene_contributors();
    verify_sprite_priorities();
    for(unsigned flags=0;flags<=9;++flags) if(!(flags&6)) verify_case(flags);
#ifdef HD2D_SCANOUT_CAPTURE
    setup(1); armed=true; flooding=true; draw(); CHECK(hd2d_capture_status(capture)==HD2D_LIMIT);
    Ppu *failed=malloc(sizeof *failed); CHECK(failed); memcpy(failed,g_ppu,sizeof *failed);
    setup(1); armed=false; draw(); CHECK(!memcmp(failed,g_ppu,sizeof *failed)); free(failed); flooding=false;
    size_t n; CHECK(!hd2d_capture_data(capture,&n) && !n);
    setup(1); CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_RECORDING);
    CHECK(hd2d_capture_line(capture,g_ppu,1)==HD2D_ORDER);
    CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_RECORDING);
    CHECK(hd2d_capture_finish(capture,g_ppu)==HD2D_ORDER);
    CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_RECORDING);
    CHECK(hd2d_capture_line(capture,g_ppu,0)==HD2D_RECORDING);
    g_ppu->mosaic=1;
    CHECK(hd2d_capture_line(capture,g_ppu,1)==HD2D_UNSUPPORTED);
    CHECK(strstr(hd2d_capture_detail(capture),"line 1: mosaic: requires 0; observed 1"));
    setup(1); CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_RECORDING);
    ++g_ppu->renderPitch;
    CHECK(hd2d_capture_line(capture,g_ppu,0)==HD2D_ORDER);
#define REJECT(field,value) do { setup(1); g_ppu->field=(value); CHECK(hd2d_capture_begin(capture,&id,g_ppu)==HD2D_UNSUPPORTED); CHECK(strstr(hd2d_capture_detail(capture),#field)); } while(0)
    REJECT(bgmode,7); REJECT(mosaic,0x11); REJECT(setini,1); REJECT(setini,8);
    REJECT(extraLeftRight,16); REJECT(renderVram,g_ppu->vram); REJECT(renderBuffer,NULL);
    REJECT(renderPitch,1000); REJECT(renderFlags,2); REJECT(extraObjectCount,1);
    REJECT(wsHudSplitHeight,0x80); REJECT(wsViewportInsetL[0],1);
    REJECT(overlayRenderBuffer[0],&pixels[0][0]); REJECT(mode7Hd.pixels,(uint32_t *)pixels);
    setup(1); test_shadow_active=true; CHECK(!hd2d_profile_supported(g_ppu)); test_shadow_active=false;
    g_snes_ppu_dbg_layer_mask=1; CHECK(!hd2d_profile_supported(g_ppu)); g_snes_ppu_dbg_layer_mask=255;
    for(fail_at=1;fail_at<=2;++fail_at) { allocation_count=0; CHECK(!hd2d_capture_new(HD2D_MAX_BYTES,fault_allocate,free)); }
    CHECK(!hd2d_capture_new(HD2D_MAX_BYTES+1,NULL,NULL));
    CHECK(!hd2d_capture_new(1,NULL,NULL));
    id.sequence=2; id.draw=3; id.epoch=8; id.guest_frame=0;
    setup(1); armed=true; draw();
    const uint8_t *b=hd2d_capture_data(capture,&n);
    CHECK(b && hd2d_read64(b+56)==8 && hd2d_read64(b+64)==2 && !hd2d_read64(b+80));
    const unsigned rounds=50;
    for(unsigned on=0;on<2;++on) {
      double elapsed=0, wall=0;
      for(unsigned i=0;i<rounds;++i) {
        struct timespec begin, end;
        setup(1); armed=on!=0;
        CHECK(clock_gettime(CLOCK_MONOTONIC,&begin)==0);
        clock_t start=clock(); draw();
        elapsed+=(double)(clock()-start)/CLOCKS_PER_SEC;
        CHECK(clock_gettime(CLOCK_MONOTONIC,&end)==0);
        wall+=(double)(end.tv_sec-begin.tv_sec)+(double)(end.tv_nsec-begin.tv_nsec)/1e9;
      }
      printf("benchmark capture=%s rounds=%u mean_cpu_ms=%.3f mean_wall_ms=%.3f\n",
        on?"on":"off",rounds,elapsed*1000/rounds,wall*1000/rounds);
    }
#endif
    puts("scanout tests: PASS");
  }
  hd2d_capture_free(capture);
#endif
  free(g_dma); free(g_ppu); return 0;
}
