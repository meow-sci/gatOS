/* vagc_shim.c — the embedded-mode seam (AGC_PLAN A6, D-A3): links yaAGC's agc_engine.c /
 * agc_engine_init.c into agc-bridge and provides every host symbol they need
 * (ChannelOutput/ChannelInput/ChannelRoutine/ShiftToDeda/RequestRadarData/BacktraceAdd/
 * DebugMode/UnblockSocket/rfopen), plus a tiny flat C API so the Rust side never has to know
 * sizeof(agc_t).
 *
 * Compiled by build.rs only with `--features embedded`, from the Virtual AGC checkout named
 * by $VIRTUALAGC (default /opt/src/virtualagc). GPLv2 code is COMPILED FROM THE CHECKOUT at
 * build time — never vendored into the gatOS repo (plan §7.2).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include "yaAGC.h"
#include "agc_engine.h"

/* ---- host globals the engine objects reference ----
 * (DebugMode is already defined by agc_engine.h's include in agc_engine.o.) */
FILE *
rfopen (const char *Filename, const char *mode)
{
  return fopen (Filename, mode);
}

void
UnblockSocket (int fd)
{
  (void) fd;
}

void
BacktraceAdd (agc_t *State, int Cause)
{
  (void) State;
  (void) Cause;
}

void
ShiftToDeda (agc_t *State, int Data)
{
  (void) State;
  (void) Data;
}

/* ---- output: every CPU channel write goes to the Rust callback ---- */
typedef void (*vagc_output_cb) (void *ctx, int channel, int value);
static vagc_output_cb OutputCb = 0;
static void *OutputCtx = 0;

void
vagc_set_output_cb (vagc_output_cb cb, void *ctx)
{
  OutputCb = cb;
  OutputCtx = ctx;
}

void
ChannelOutput (agc_t *State, int Channel, int Value)
{
  /* Channel 7 (SUPERBNK) is an output channel with a purpose INSIDE the CPU — the write must
   * land back in InputChannel[7]/OutputChannel7 or superbank switching breaks and the rope
   * TC-traps into an endless restart loop (the exact special-case SocketAPI.c:114-118 has). */
  if (Channel == 7)
    {
      State->InputChannel[7] = State->OutputChannel7 = (Value & 0160);
      return;
    }
  if (OutputCb)
    OutputCb (OutputCtx, Channel, Value);
}

/* Inputs are pushed synchronously through vagc_* below; nothing to poll. */
int
ChannelInput (agc_t *State)
{
  (void) State;
  return 0;
}

void
ChannelRoutine (agc_t *State)
{
  (void) State;
}

/* ---- radar: the designed embedded hook (SocketAPI.c:508-516) — the bridge keeps the four
 * words (select codes 4..7 → index 0..3) fresh each tick; the engine calls us inside the
 * 9-gate window and we deposit RNRAD synchronously. ---- */
static uint16_t RadarWords[4] = { 0, 0, 0, 0 };
static int RadarValid = 0;

void
vagc_set_radar_words (const uint16_t words[4], int valid)
{
  memcpy (RadarWords, words, sizeof RadarWords);
  RadarValid = valid;
}

void
RequestRadarData (agc_t *State)
{
  int code = State->InputChannel[013] & 07;
  if (RadarValid && code >= 4 && code <= 7)
    State->Erasable[0][RegRNRAD] = RadarWords[code - 4] & 077777;
}

/* ---- the flat API the Rust EmbeddedPort drives ---- */

agc_t *
vagc_new (void)
{
  return (agc_t *) calloc (1, sizeof (agc_t));
}

void
vagc_free (agc_t *State)
{
  free (State);
}

int
vagc_init (agc_t *State, const char *rope, const char *core, int all_or_erasable)
{
  return agc_engine_init (State, rope, core, all_or_erasable);
}

/* Runs `cycles` machine cycles (1 MCT = 11.7 µs each). */
void
vagc_step (agc_t *State, int cycles)
{
  int i;
  for (i = 0; i < cycles; i++)
    agc_engine (State);
}

/* Masked channel write — the same replace-only-masked-bits semantics a socket u-packet pair
 * gets (SocketAPI.c:219-238), including KEYRUPT1 on ch 015 and INLINK+UPRUPT on 0173. */
void
vagc_write_io (agc_t *State, int channel, int value, int mask)
{
  int merged;
  value &= 077777;
  mask &= 077777;
  if (channel == 0173)
    {
      State->Erasable[0][RegINLINK] = value;
      State->InterruptRequests[7] = 1;
      return;
    }
  merged = (value & mask) | (ReadIO (State, channel) & ~mask);
  WriteIO (State, channel, merged);
  if (channel == 015)
    State->InterruptRequests[5] = 1;
}

/* Unprogrammed counter increment (PINC/PCDU/…): `counter` is the erasable register (e.g. 037),
 * `inc_type` the same code a socket counter-packet carries. */
void
vagc_counter (agc_t *State, int counter, int inc_type)
{
  UnprogrammedIncrement (State, 0200 | counter, inc_type);
}

int
vagc_read_io (agc_t *State, int channel)
{
  return ReadIO (State, channel);
}

int
vagc_read_erasable (agc_t *State, int bank, int offset)
{
  if (bank < 0 || bank > 7 || offset < 0 || offset >= 0400)
    return 0;
  return State->Erasable[bank][offset];
}

void
vagc_write_erasable (agc_t *State, int bank, int offset, int value)
{
  if (bank < 0 || bank > 7 || offset < 0 || offset >= 0400)
    return;
  State->Erasable[bank][offset] = value & 077777;
}

int
vagc_make_core_dump (agc_t *State, const char *path)
{
  MakeCoreDump (State, path);
  return 0;
}

/* Debug/status accessors. */
int
vagc_interrupt_pending (agc_t *State, int n)
{
  if (n < 0 || n > 10)
    return 0;
  return State->InterruptRequests[n];
}

long long
vagc_cycles (agc_t *State)
{
  return (long long) State->CycleCounter;
}
