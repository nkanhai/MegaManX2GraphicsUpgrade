/* Desktop-only trigger. No PPU or guest state is accessed here. */
#ifndef HD2D_HOTKEY_H
#define HD2D_HOTKEY_H
#include <stdbool.h>
/* Owner thread: install an observer if needed, then consume one pending request.
 * SDL's callback only changes atomic flags; repeated requests coalesce. */
bool hd2d_hotkey_poll(void);
void hd2d_hotkey_stop(void);
#endif
