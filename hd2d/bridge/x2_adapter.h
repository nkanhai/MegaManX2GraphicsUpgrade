#ifndef HD2D_X2_ADAPTER_H
#define HD2D_X2_ADAPTER_H
#ifdef HD2D_SCANOUT_CAPTURE
void hd2d_x2_begin(void);
void hd2d_x2_line(unsigned line);
void hd2d_x2_finish(void);
void hd2d_x2_shutdown(void);
#else
#define hd2d_x2_begin() ((void)0)
#define hd2d_x2_line(line) ((void)0)
#define hd2d_x2_finish() ((void)0)
#define hd2d_x2_shutdown() ((void)0)
#endif
#endif
