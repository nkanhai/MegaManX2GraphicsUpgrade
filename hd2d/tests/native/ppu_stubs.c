/* Standalone upstream PPU dependencies. No ROM, SDL, or runtime state. */
#include "snes/ppu.h"
#include "snes/snes.h"
#include "snes/ws_shadow.h"
Snes *g_snes;
int snes_frame_counter;
unsigned char g_snesrecomp_last_hdmaen;
bool test_shadow_active;
uint16_t WsShadowTile(int layer,int x,uint32_t y,uint16_t scroll,uint16_t address,uint16_t tile) {
  (void)layer; (void)x; (void)y; (void)scroll; (void)address; return tile;
}
bool WsShadowLayerActive(int layer) { (void)layer; return test_shadow_active; }
uint32_t WsShadowWorldX(int layer) { (void)layer; return 0; }
uint32_t WsShadowPresentWorldY(int layer,int x) { (void)layer; (void)x; return 0; }
uint32_t WsShadowScrollY(int layer) { (void)layer; return 0; }
void WsShadowOnVramWrite(uint16_t address,uint16_t value) { (void)address; (void)value; }
