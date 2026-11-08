#ifndef FGS_GPU_TABLE_H
#define FGS_GPU_TABLE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Independent, dependency-free representation of ONE AV1 filmgrn1 event. */
typedef struct FgsGrainModel {
    uint16_t seed;
    int lag, ar_shift, grain_shift, scaling_shift, chroma_from_luma, overlap;
    int uv_mult[2], uv_luma_mult[2], uv_offset[2];
    int points_count[3];
    uint8_t points[3][14][2];
    int coefficients_count[3];
    int8_t coefficients[3][25];
} FgsGrainModel;

/* Refuses multi-event tables: previewing the wrong timeline event is unsafe. */
bool fgs_parse_table(const char *text, FgsGrainModel *out,
                     char *error, size_t error_size);

/* Selects the active model at 100-ns timeline ticks. Handles multi-event
 * built-in Film Presets and AV1 update=0 model reuse without changing E seed.
 * Rejects gaps, disabled events, invalid ordering and missing model updates. */
bool fgs_parse_table_at(const char *text, uint64_t ticks, FgsGrainModel *out,
                        char *error, size_t error_size);

/* Same validated lookup, additionally returning the selected event start.
 * grav1synth advances each event's seed for every frame in that event. */
bool fgs_parse_table_at_event(const char *text, uint64_t ticks, FgsGrainModel *out,
                              uint64_t *event_start, char *error, size_t error_size);

/* Approximate grav1synth's per-frame wrapping seed update using a CFR rate.
 * The first frame of each event receives base_seed + 10956. Exact VFR packet
 * indexing cannot be recovered from a timestamp and nominal FPS alone. */
uint16_t fgs_seed_for_frame(uint16_t base_seed, uint64_t ticks,
                             uint64_t event_start, uint32_t fps_num, uint32_t fps_den);

/* Mirrors FGS Av1GrainStrengthCore.ScaleTable: only Y point ordinates change. */
void fgs_scale_model(const FgsGrainModel *source, int percent, FgsGrainModel *dest);

#endif
