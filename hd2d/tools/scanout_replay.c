/* Offline CLI. Exit 0: exact match, 1: valid reconstruction differs, 2: error. */
#include "scanout.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc,char **argv) {
  if(argc!=2 && !(argc==4 && !strcmp(argv[2],"--rgba-output"))) {
    fprintf(stderr,"usage: scanout_replay CAPTURE [--rgba-output NEW_FILE]\n"); return 2;
  }
  FILE *f=fopen(argv[1],"rb");
  if(!f) { perror("capture"); return 2; }
  if(fseek(f,0,SEEK_END)) { fclose(f); return 2; }
  long length=ftell(f);
  if(length<0 || length>HD2D_MAX_BYTES || fseek(f,0,SEEK_SET)) { fclose(f); return 2; }
  uint8_t *b=malloc((size_t)length+1), *rgba=malloc(HD2D_RGBA_BYTES);
  if(!b || !rgba) { free(b); free(rgba); fclose(f); return 2; }
  bool read_ok=fread(b,1,(size_t)length,f)==(size_t)length;
  fclose(f);
  char detail[256]="read failed";
  Hd2dReplayResult result=read_ok ? hd2d_reconstruct(b,(size_t)length,rgba,detail,sizeof detail) : HD2D_REPLAY_INVALID;
  puts(detail);
  if(argc==4 && result!=HD2D_REPLAY_INVALID) {
    FILE *out=fopen(argv[3],"wbx");
    bool ok=false;
    if(out) { ok=fwrite(rgba,1,HD2D_RGBA_BYTES,out)==HD2D_RGBA_BYTES; if(fclose(out)) ok=false; }
    if(!ok) {
      if(out) remove(argv[3]); /* Never remove a pre-existing output. */
      fprintf(stderr,"could not publish new RGBA output\n"); result=HD2D_REPLAY_INVALID;
    }
  }
  free(b); free(rgba);
  return result==HD2D_REPLAY_MATCH ? 0 : result==HD2D_REPLAY_MISMATCH ? 1 : 2;
}
