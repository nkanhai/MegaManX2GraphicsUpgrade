#include "hotkey.h"
#include "desktop/sdl_compat.h"
#include "desktop/config.h"
#include "keybinds.h"
#include <stdatomic.h>
#include <stdio.h>

static atomic_bool pending, held;
static atomic_uint window_id;
static bool watching, warned;

#if SNESRECOMP_SDL3
static bool SDLCALL observe(void *unused, SDL_Event *event) {
#else
static int SDLCALL observe(void *unused, SDL_Event *event) {
#endif
  (void)unused;
  /* May run on an SDL producer thread. No allocation, file I/O, logging,
   * keymap reads, SDL calls, or guest access is permitted in this callback. */
#if SNESRECOMP_SDL3
  bool lost=event->type==SDL_EVENT_WINDOW_FOCUS_LOST;
#else
  bool lost=event->type==SDL_WINDOWEVENT && event->window.event==SDL_WINDOWEVENT_FOCUS_LOST;
#endif
  if(lost && event->window.windowID==atomic_load(&window_id)) atomic_store(&held,false);
  if((event->type==SDL_KEYDOWN || event->type==SDL_KEYUP) &&
     event->key.windowID==atomic_load(&window_id) && SNESRECOMP_SDL_EVENT_KEY(*event)==SDLK_F10) {
    if(event->type==SDL_KEYUP) atomic_store(&held,false);
    else if(!atomic_exchange(&held,true) && !SNESRECOMP_SDL_EVENT_REPEAT(*event)) {
      unsigned mod=SNESRECOMP_SDL_EVENT_MOD(*event);
      /* Left modifiers avoid the stock Right Shift = Select binding. */
      if((mod & (KMOD_CTRL|KMOD_SHIFT|KMOD_ALT|KMOD_GUI))==(KMOD_LCTRL|KMOD_LSHIFT))
        atomic_store(&pending,true);
    }
  }
  return true; /* Event watches observe; they never consume/modify events. */
}

static bool conflicts(void) {
  /* Controller bindings use raw scancodes, independently of [KeyMap] and
   * regardless of modifiers. Check every seat, even currently inactive ones. */
  for(int player=1;player<=KEYBINDS_MAX_PLAYERS;++player)
    for(int button=0;button<keybinds_button_count();++button) {
      SDL_Scancode key=keybinds_get_button(player,button);
      if(key==SDL_SCANCODE_F10 || key==SDL_SCANCODE_LCTRL || key==SDL_SCANCODE_LSHIFT) return true;
    }
  if(FindCmdForSdlKey(SDLK_F10,(SDL_Keymod)(KMOD_LCTRL|KMOD_LSHIFT))) return true;
  /* Also protect custom bindings to the modifier keys themselves, regardless
   * of their press order. The lookup ignores a modifier key's own modifier. */
  return FindCmdForSdlKey(SDLK_LCTRL,KMOD_NONE) ||
         FindCmdForSdlKey(SDLK_LCTRL,KMOD_LSHIFT) ||
         FindCmdForSdlKey(SDLK_LSHIFT,KMOD_NONE) ||
         FindCmdForSdlKey(SDLK_LSHIFT,KMOD_LCTRL);
}
bool hd2d_hotkey_poll(void) {
  if(!(SDL_WasInit(SDL_INIT_VIDEO)&SDL_INIT_VIDEO)) return false;
  SDL_Window *window=SDL_GetKeyboardFocus();
  unsigned next=window ? SDL_GetWindowID(window) : 0;
  if(atomic_exchange(&window_id,next)!=next) atomic_store(&held,false);
  if(!watching) {
    atomic_store(&pending,false); atomic_store(&held,false);
#if SNESRECOMP_SDL3
    if(!SDL_AddEventWatch(observe,NULL)) return false;
#else
    SDL_AddEventWatch(observe,NULL);
#endif
    watching=true;
  }
  bool requested=atomic_exchange(&pending,false);
  bool conflict=conflicts();
  if(conflict && !warned) fprintf(stderr,"[hd2d] shortcut disabled: keybinds.ini/[KeyMap] binds F10, Left Ctrl, Left Shift or Ctrl+Shift+F10\n");
  warned=conflict;
  return requested && !conflict;
}
void hd2d_hotkey_stop(void) {
  if(watching) {
#if SNESRECOMP_SDL3
    SDL_RemoveEventWatch(observe,NULL);
#else
    SDL_DelEventWatch(observe,NULL);
#endif
  }
  watching=warned=false;
  atomic_store(&pending,false); atomic_store(&held,false); atomic_store(&window_id,0);
}
