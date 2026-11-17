#include "scanout.h"
#include <stdio.h>
#include <stdlib.h>
int main(int argc,char **argv) {
  if(argc!=2) return 2;
  FILE *f=fopen(argv[1],"rb");
  if(!f) return 2;
  if(fseek(f,0,SEEK_END)) { fclose(f); return 2; }
  long length=ftell(f);
  if(length<0 || length>HD2D_MAX_BYTES || fseek(f,0,SEEK_SET)) { fclose(f); return 2; }
  uint8_t *b=malloc((size_t)length+1), *rgba=malloc(HD2D_RGBA_BYTES);
  if(!b || !rgba) { free(b); free(rgba); fclose(f); return 2; }
  bool read_ok=fread(b,1,(size_t)length,f)==(size_t)length;
  fclose(f);
  char error[160];
  bool ok=read_ok && hd2d_replay(b,(size_t)length,rgba,error,sizeof error);
  puts(read_ok ? error : "read failed");
  free(b); free(rgba);
  return ok ? 0 : 1;
}
