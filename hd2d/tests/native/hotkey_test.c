#include "hotkey.h"
#include "desktop/sdl_compat.h"
#include "keybinds.h"
#include <stdio.h>
#include <stdlib.h>
#include <stdatomic.h>
#define CHECK(x) do { if(!(x)) { fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); exit(1); } } while(0)
static SDL_Keycode conflict_key;
int FindCmdForSdlKey(SDL_Keycode key, SDL_Keymod mod) {
  (void)mod; return key==conflict_key;
}
static unsigned pushed;
static atomic_bool producer_done;
static void key(bool down, SDL_Keycode code, unsigned mod, bool repeat, unsigned window) {
  SDL_Event event={0}; event.type=down ? SDL_KEYDOWN : SDL_KEYUP;
  event.key.keysym.sym=code; event.key.keysym.mod=mod;
  event.key.repeat=repeat; event.key.windowID=window;
  CHECK(SDL_PushEvent(&event)==1); ++pushed;
}
static void press(void) {
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,0);
  key(false,SDLK_F10,0,false,0);
}
static int SDLCALL threaded(void *unused) {
  (void)unused;
  for(unsigned i=0;i<100;++i) press();
  atomic_store(&producer_done,true);
  return 0;
}
int main(void) {
  CHECK(SDL_Init(SDL_INIT_VIDEO)==0);
  for(int player=1;player<=KEYBINDS_MAX_PLAYERS;++player) keybinds_reset_player(player);
  CHECK(!hd2d_hotkey_poll());
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,0);
  CHECK(hd2d_hotkey_poll());
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,true,0);
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,0);
  CHECK(!hd2d_hotkey_poll()); /* Repeat/duplicate keydown while held. */
  key(false,SDLK_F10,0,false,0);
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,0); CHECK(hd2d_hotkey_poll());
  SDL_Event lost={0}; lost.type=SDL_WINDOWEVENT; lost.window.event=SDL_WINDOWEVENT_FOCUS_LOST;
  CHECK(SDL_PushEvent(&lost)==1);
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,0); CHECK(hd2d_hotkey_poll());
  key(false,SDLK_F10,0,false,0);
  press(); press(); CHECK(hd2d_hotkey_poll()); CHECK(!hd2d_hotkey_poll());
  const unsigned rejected[]={0,KMOD_LCTRL,KMOD_LSHIFT,KMOD_RSHIFT|KMOD_LCTRL,
    KMOD_RCTRL|KMOD_LSHIFT,KMOD_LCTRL|KMOD_LSHIFT|KMOD_ALT,
    KMOD_LCTRL|KMOD_LSHIFT|KMOD_GUI};
  for(unsigned i=0;i<sizeof rejected/sizeof *rejected;++i) {
    key(true,SDLK_F10,rejected[i],false,0); key(false,SDLK_F10,0,false,0);
    CHECK(!hd2d_hotkey_poll());
  }
  key(true,SDLK_F9,KMOD_LCTRL|KMOD_LSHIFT,false,0);
  key(true,SDLK_F10,KMOD_LCTRL|KMOD_LSHIFT,false,99); CHECK(!hd2d_hotkey_poll());
  for(unsigned i=0;i<3;++i) {
    conflict_key=(SDL_Keycode[]){SDLK_F10,SDLK_LCTRL,SDLK_LSHIFT}[i];
    press(); CHECK(!hd2d_hotkey_poll());
  }
  conflict_key=0; press(); CHECK(hd2d_hotkey_poll());
  for(unsigned i=0;i<3;++i) {
    keybinds_set_button(8,0,(SDL_Scancode[]){SDL_SCANCODE_F10,SDL_SCANCODE_LCTRL,SDL_SCANCODE_LSHIFT}[i]);
    press(); CHECK(!hd2d_hotkey_poll());
  }
  keybinds_reset_player(8); press(); CHECK(hd2d_hotkey_poll());
  /* A producer thread can queue while the owner polls: no SDL event is eaten. */
  SDL_Thread *thread=SDL_CreateThread(threaded,"synthetic-key-events",NULL); CHECK(thread);
  unsigned requests=0;
  do { requests+=hd2d_hotkey_poll(); SDL_Delay(1); } while(!atomic_load(&producer_done));
  int result; SDL_WaitThread(thread,&result); CHECK(!result);
  requests+=hd2d_hotkey_poll(); CHECK(requests && requests<=100); CHECK(!hd2d_hotkey_poll());
  SDL_Event event; unsigned received=0;
  while(SDL_PollEvent(&event)) if(event.type==SDL_KEYDOWN || event.type==SDL_KEYUP) ++received;
  CHECK(received==pushed);
  press(); hd2d_hotkey_stop(); CHECK(!hd2d_hotkey_poll()); /* Shutdown drops pending. */
  press(); CHECK(hd2d_hotkey_poll()); /* Reinstall after a host relaunch. */
  hd2d_hotkey_stop(); hd2d_hotkey_stop(); SDL_Quit();
  puts("hotkey edges/coalescing/modifiers/conflicts/event preservation/thread handoff/lifecycle: PASS");
  return 0;
}
